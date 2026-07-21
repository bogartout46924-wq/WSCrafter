# WSCrafter

WSCrafter is a GameHelper plugin for Path of Exile 2 waystone crafting.

It helps locate crafting currency, plan selected inventory slots, and automate the basic waystone crafting flow:

- Use Orb of Alchemy on normal or magic waystones.
- Use Exalted Orbs on rare waystones until the target explicit mod count is reached.
- Use a Vaal Orb as the final step.
- Skip waystones that have already had a Vaal attempt during the current run.

## Development

Place this repository under `Plugins/WSCrafter` inside a GameHelper checkout, then build:

```powershell
dotnet build .\Plugins\WSCrafter\WSCrafter.csproj -c Release --no-restore
```

The project references `GameHelper` and `GameOffsets` from the parent checkout.
