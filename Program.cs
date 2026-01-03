using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System;

if (args.Length == 0) {
    Console.WriteLine("Usage: dotnet run -- <path-to-file.xlsx>");
    return;
}

string filepath = args[0];
try {
    using (SpreadsheetDocument doc = SpreadsheetDocument.Open(filepath, false)) {
        OpenXmlValidator validator = new OpenXmlValidator();
        var errors = validator.Validate(doc);
        int count = 0;

        foreach (var error in errors) {
            count++;
            Console.WriteLine($"Error {count}: {error.Description}");
            Console.WriteLine($"Path: {error.Path?.XPath}");
            Console.WriteLine($"Part: {error.Part?.Uri}\n-----------------");
        }

        if (count == 0) Console.WriteLine("Document is valid!");
        else Console.WriteLine($"Found {count} schema errors.");
    }
} catch (Exception ex) {
    Console.WriteLine($"Error opening file: {ex.Message}");
}
