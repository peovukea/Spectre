using Avro;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: GenerateCdm18 <schema.avsc> <output-directory>");
    return 1;
}

var schemaPath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);

Directory.CreateDirectory(outputDirectory);

var codeGen = new CodeGen();
codeGen.AddSchema(File.ReadAllText(schemaPath));
codeGen.GenerateCode();
codeGen.WriteTypes(outputDirectory, skipDirectories: true);

Console.WriteLine($"Generated CDM18 classes in {outputDirectory}");
return 0;
