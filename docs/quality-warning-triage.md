# Warning Triage Backlog

This document tracks the post-stability warning cleanup batches.

## Batch A: Unawaited Tasks (`CS4014`)
- `SonosControl.Web/Pages/ConfigPage.razor`

## Batch B: Nullable Dereference and Assignment (`CS8602`, `CS8604`, `CS8601`)
- `SonosControl.Web/Pages/IndexPage.razor`
- `SonosControl.Web/Pages/UserEdit.razor`
- `SonosControl.Web/Pages/ConfigPage.razor`
- `SonosControl.Web/Services/SonosControlService.cs`

## Batch C: Non-nullable Initialization (`CS8618`)
- `SonosControl.Web/Models/ApplicationUser.cs`
- `SonosControl.Web/Models/LoginModel.cs`
- `SonosControl.Web/Pages/Register.razor`

## Batch D: Dead Fields (`CS0169`, `CS0414`)
- `SonosControl.Web/Pages/ConfigPage.razor`
- `SonosControl.Web/Pages/IndexPage.razor`

## Deferred
- `ASP0014` in `SonosControl.Web/Program.cs` remains low-priority until route registration refactor work is scheduled.
