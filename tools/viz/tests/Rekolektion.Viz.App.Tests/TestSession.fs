module Rekolektion.Viz.App.Tests.TestSession

open Avalonia.Headless

/// Single Avalonia.Headless session shared by every test in this
/// assembly. Created lazily on first access. The `avares://` URI
/// scheme parser registers globally on session start and refuses a
/// second registration, so test classes must NOT spin up their own
/// session — they reference `instance` here instead.
let instance : Lazy<HeadlessUnitTestSession> =
    lazy HeadlessUnitTestSession.StartNew(typeof<Rekolektion.Viz.App.HeadlessApp>)
