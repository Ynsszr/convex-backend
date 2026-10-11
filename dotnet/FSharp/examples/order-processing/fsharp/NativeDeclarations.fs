namespace OrderProcessing

open Convex.FSharp

module NativeDeclarations =
    let schema=Schema.define [
        (TableSchema.defineForTable Tables.products).WithIndex("by_sku", ["Sku"])
        (TableSchema.defineForTable Tables.orders).WithIndex("by_customer", ["CustomerEmail"])
        (TableSchema.defineForTable Tables.auditLogs).WithIndex("by_order", ["OrderId"])
    ]
    let app=NativeComponentDefinitions.app [] [] NativeComponentDefinitions.emptyExports None
    let functions = [|
        "createProduct",FunctionRegistration.erase Functions.createProduct
        "getProduct",FunctionRegistration.erase Functions.getProduct
        "getProductBySku",FunctionRegistration.erase Functions.getProductBySku
        "listProducts",FunctionRegistration.erase Functions.listProducts
        "placeOrder",FunctionRegistration.erase Functions.placeOrder
        "getOrder",FunctionRegistration.erase Functions.getOrder
        "listOrdersByCustomer",FunctionRegistration.erase Functions.listOrdersByCustomer
        "paginateOrders",FunctionRegistration.erase Functions.paginateOrders
        "autoFulfillOrder",FunctionRegistration.erase Functions.autoFulfillOrder
        "processPayment",FunctionRegistration.erase Functions.processPayment
        "cancelOrder",FunctionRegistration.erase Functions.cancelOrder
        "listAuditLogs",FunctionRegistration.erase Functions.listAuditLogs
        "checkoutAction",FunctionRegistration.erase Functions.checkoutAction
    |]
    let modules=[|"functions","OrderProcessing.Functions",functions|]
