namespace OrderProcessing

open System
open Convex.FSharp

type Product =
    {
        Sku: string
        Title: string
        PriceCents: int64
        Stock: int64
    }

type OrderItem =
    {
        ProductId: Id<Product>
        Quantity: int64
        PriceCents: int64
    }

type OrderStatus =
    | Pending
    | Processing of paymentId: string
    | Fulfilled of trackingNumber: string
    | Cancelled of reason: string

type Order =
    {
        CustomerEmail: string
        Items: OrderItem list
        TotalCents: int64
        Status: OrderStatus
        CreatedAt: CommitTimestamp
        ShippedAt: int64 option
    }

type AuditLog =
    {
        OrderId: Id<Order>
        Action: string
        CommitTs: CommitTimestamp
    }

module Tables =
    let products = Table.define<Product> "products"
    let bySku = products.Index("by_sku", [ "Sku" ])

    let orders = Table.define<Order> "orders"
    let byCustomer = orders.Index("by_customer", [ "CustomerEmail" ])

    let auditLogs = Table.define<AuditLog> "audit_logs"
    let byOrder = auditLogs.Index("by_order", [ "OrderId" ])

type CreateProductArgs =
    {
        Sku: string
        Title: string
        PriceCents: int64
        Stock: int64
    }

type ProductIdArgs =
    {
        Id: Id<Product>
    }

type SkuArgs =
    {
        Sku: string
    }

type PlaceOrderItemArg =
    {
        ProductId: Id<Product>
        Quantity: int64
    }

type PlaceOrderArgs =
    {
        CustomerEmail: string
        Items: PlaceOrderItemArg list
    }

type OrderIdArgs =
    {
        Id: Id<Order>
    }

type ProcessPaymentArgs =
    {
        OrderId: Id<Order>
        PaymentId: string
    }

type FulfillOrderArgs =
    {
        OrderId: Id<Order>
        TrackingNumber: string
    }

type CancelOrderArgs =
    {
        OrderId: Id<Order>
        Reason: string
    }

type CustomerEmailArgs =
    {
        CustomerEmail: string
    }

type PaginateOrdersArgs =
    {
        NumItems: int
        Cursor: string option
    }
