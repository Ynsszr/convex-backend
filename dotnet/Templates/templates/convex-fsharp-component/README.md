# ConvexComponent

Native reusable Convex component. Mount it from a parent application's F# app
declaration and list this `convex.project.json` in the parent's `components`
configuration. Build/deploy the parent graph; component storage is isolated by
the original Convex component owner.

This component exports `counter.read` and `counter.increment`. Parent-generated
`Components` references have internal visibility and are called inside parent
queries/mutations. They are not browser-callable functions. Expose an authorized
parent function when needed.
