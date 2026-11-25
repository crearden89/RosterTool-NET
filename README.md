# Roster — .NET edition

The roster planning tool rebuilt in C# on .NET 10, with SQL Server and a Blazor web app.
It follows the rules of the TypeScript version exactly; the parity tests prove it.

- [ASSUMPTIONS.md](ASSUMPTIONS.md): the technology choices and where each can change.
- [CONVENTIONS.md](CONVENTIONS.md): how the code is organised, who owns which folder, and the
  JavaScript-to-C# rules that keep the numbers identical.

## Build and test

Requires the .NET 10 SDK.

```
dotnet build Roster.sln
dotnet test Roster.sln
```
