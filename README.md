Build: `dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true`

Run: `./bin/Release/net10.0/osx-arm64/publish/OOXMLValidator /tmp/test.xlsx`

Or: `options("openxlsx2.ooxml_validator" = "~/Source/OOXMLValidator/bin/Release/net10.0/osx-arm64/OOXMLValidator")`
