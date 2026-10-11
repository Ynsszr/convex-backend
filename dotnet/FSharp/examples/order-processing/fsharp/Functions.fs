namespace OrderProcessing

open System
open Convex.FSharp
open Tables

module Functions =

    let createProduct =
        Convex.mutation (fun ctx (args: CreateProductArgs) ->
            async {
                let (doc: Product) = {
                    Sku = args.Sku
                    Title = args.Title
                    PriceCents = args.PriceCents
                    Stock = args.Stock
                }
                let! id = ctx.Db.Insert(products, doc)
                return Id.value id
            }
        )

    let getProduct =
        Convex.query (fun ctx (args: ProductIdArgs) ->
            ctx.Db.Get(products, args.Id)
        )

    let getProductBySku =
        Convex.query (fun ctx (args: SkuArgs) ->
            ctx.Db.Query(products)
                .WithIndex("by_sku", fun r -> r.Eq("Sku", box args.Sku))
                .First()
        )

    let listProducts =
        Convex.query (fun ctx () ->
            ctx.Db.Query(products).Collect()
        )

    let placeOrder =
        Convex.mutation (fun ctx (args: PlaceOrderArgs) ->
            async {
                let mutable totalCents = 0L
                let mutable resolvedItems: OrderItem list = []

                for item in args.Items do
                    let! prodOpt = ctx.Db.Get(products, item.ProductId)
                    match prodOpt with
                    | None ->
                        return Errors.failWithConvexError (sprintf "Product %s not found" (Id.value item.ProductId))
                    | Some prod ->
                        if prod.Stock < item.Quantity then
                            return Errors.failWithConvexError (sprintf "Insufficient stock for product %s: required %d, available %d" prod.Sku item.Quantity prod.Stock)
                        let newStock = prod.Stock - item.Quantity
                        do! ctx.Db.Patch(item.ProductId, {| Stock = newStock |})
                        let lineTotal = prod.PriceCents * item.Quantity
                        totalCents <- totalCents + lineTotal
                        resolvedItems <- { ProductId = item.ProductId; Quantity = item.Quantity; PriceCents = prod.PriceCents } :: resolvedItems

                let (orderDoc: Order) = {
                    CustomerEmail = args.CustomerEmail
                    Items = List.rev resolvedItems
                    TotalCents = totalCents
                    Status = OrderStatus.Pending
                    CreatedAt = ctx.Db.Vars.CommitTs
                    ShippedAt = None
                }
                let! orderId = ctx.Db.Insert(orders, orderDoc)

                let (auditDoc: AuditLog) = {
                    OrderId = orderId
                    Action = "ORDER_PLACED"
                    CommitTs = ctx.Db.Vars.CommitTs
                }
                let! _ = ctx.Db.Insert(auditLogs, auditDoc)

                return Id.value orderId
            }
        )

    let getOrder =
        Convex.query (fun ctx (args: OrderIdArgs) ->
            ctx.Db.Get(orders, args.Id)
        )

    let listOrdersByCustomer =
        Convex.query (fun ctx (args: CustomerEmailArgs) ->
            ctx.Db.Query(orders)
                .WithIndex("by_customer", fun r -> r.Eq("CustomerEmail", box args.CustomerEmail))
                .Collect()
        )

    let paginateOrders =
        Convex.query (fun ctx (args: PaginateOrdersArgs) ->
            ctx.Db.Query(orders)
                .Order(QueryOrder.Desc)
                .Paginate({ NumItems = args.NumItems; Cursor = args.Cursor })
        )

    let autoFulfillOrder =
        Convex.internalMutation (fun ctx (args: FulfillOrderArgs) ->
            async {
                let! orderOpt = ctx.Db.Get(orders, args.OrderId)
                match orderOpt with
                | Some (_: Order) ->
                    do! ctx.Db.Patch(
                        args.OrderId,
                        {|
                            Status = OrderStatus.Fulfilled args.TrackingNumber
                            ShippedAt = Some 1727400000000L
                        |}
                    )
                    let (auditDoc: AuditLog) = {
                        OrderId = args.OrderId
                        Action = sprintf "ORDER_FULFILLED: %s" args.TrackingNumber
                        CommitTs = ctx.Db.Vars.CommitTs
                    }
                    let! _ = ctx.Db.Insert(auditLogs, auditDoc)
                    return ()
                | None ->
                    return ()
            }
        )

    let processPayment =
        Convex.mutation (fun ctx (args: ProcessPaymentArgs) ->
            async {
                let! (orderOpt: Order option) = ctx.Db.Get(orders, args.OrderId)
                match orderOpt with
                | None ->
                    return Errors.failWithConvexError "Order not found"
                | Some order ->
                    match order.Status with
                    | OrderStatus.Pending ->
                        do! ctx.Db.Patch(args.OrderId, {| Status = OrderStatus.Processing args.PaymentId |})
                        let (auditDoc: AuditLog) = {
                            OrderId = args.OrderId
                            Action = sprintf "PAYMENT_PROCESSED: %s" args.PaymentId
                            CommitTs = ctx.Db.Vars.CommitTs
                        }
                        let! _ = ctx.Db.Insert(auditLogs, auditDoc)

                        let fulfillPayload = { OrderId = args.OrderId; TrackingNumber = sprintf "TRK-%s" (Id.value args.OrderId) }
                        let! _ = ctx.Scheduler.RunAfter(500L, FunctionRegistration.reference "functions:autoFulfillOrder" autoFulfillOrder, fulfillPayload)
                        return ()
                    | _ ->
                        return Errors.failWithConvexError "Order cannot be processed: not in Pending state"
            }
        )

    let cancelOrder =
        Convex.mutation (fun ctx (args: CancelOrderArgs) ->
            async {
                let! (orderOpt: Order option) = ctx.Db.Get(orders, args.OrderId)
                match orderOpt with
                | None ->
                    return Errors.failWithConvexError "Order not found"
                | Some order ->
                    match order.Status with
                    | OrderStatus.Fulfilled _ ->
                        return Errors.failWithConvexError "Cannot cancel fulfilled order"
                    | OrderStatus.Cancelled _ ->
                        return Errors.failWithConvexError "Order is already cancelled"
                    | _ ->
                        for item in order.Items do
                            let! prodOpt = ctx.Db.Get(products, item.ProductId)
                            match prodOpt with
                            | Some prod ->
                                do! ctx.Db.Patch(item.ProductId, {| Stock = prod.Stock + item.Quantity |})
                            | None -> ()

                        do! ctx.Db.Patch(args.OrderId, {| Status = OrderStatus.Cancelled args.Reason |})
                        let (auditDoc: AuditLog) = {
                            OrderId = args.OrderId
                            Action = sprintf "ORDER_CANCELLED: %s" args.Reason
                            CommitTs = ctx.Db.Vars.CommitTs
                        }
                        let! _ = ctx.Db.Insert(auditLogs, auditDoc)
                        return ()
            }
        )

    let listAuditLogs =
        Convex.query (fun ctx (args: OrderIdArgs) ->
            ctx.Db.Query(auditLogs)
                .WithIndex("by_order", fun r -> r.Eq("OrderId", box (Id.value args.Id)))
                .Collect()
        )

    let checkoutAction =
        Convex.action (fun ctx (args: PlaceOrderArgs) ->
            async {
                let! _ = ctx.RunQuery(FunctionRegistration.reference "functions:listProducts" listProducts, ())
                let! orderId = ctx.RunMutation(FunctionRegistration.reference "functions:placeOrder" placeOrder, args)
                let! (orderOpt: Order option) = ctx.RunQuery(FunctionRegistration.reference "functions:getOrder" getOrder, { Id = Id.ofString orderId })
                return orderOpt
            }
        )
