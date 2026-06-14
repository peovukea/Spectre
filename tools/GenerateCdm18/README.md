# GenerateCdm18

Explicit developer tool that regenerates Apache Avro specific-record C# classes
from the repository-root `TCCDMDatum.avsc` schema.

Run from the repository root:

```powershell
dotnet run --project tools/GenerateCdm18
```

Generated files are written to
`Spectre.CdmIngestion/Generated/Cdm18` and should be reviewed and committed.
The original schema namespace is preserved because Apache Avro specific-record
deserialization resolves generated types by that namespace. Normal solution
builds do not run this tool.
