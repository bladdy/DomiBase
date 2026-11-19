using System.Text.Json;
using DomiBase.Application.Runtime;

namespace DomiBase.UnitTests;

public class RuntimeJsonValuesTests
{
    [Fact]
    public void ReadObject_ConvertsScalarsToInvariantText()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "nombre": "Ana",
              "edad": 34,
              "precio": 12.50,
              "activo": true,
              "inactivo": false,
              "notas": null,
              "meta": { "a": 1 },
              "tags": ["x", "y"]
            }
            """);

        var values = RuntimeJsonValues.ReadObject(document.RootElement);

        Assert.Equal("Ana", values["nombre"]);
        Assert.Equal("34", values["edad"]);
        Assert.Equal("12.50", values["precio"]);
        Assert.Equal("true", values["activo"]);
        Assert.Equal("false", values["inactivo"]);
        Assert.Null(values["notas"]);

        using var meta = JsonDocument.Parse(values["meta"]!);
        Assert.Equal(1, meta.RootElement.GetProperty("a").GetInt32());

        using var tags = JsonDocument.Parse(values["tags"]!);
        Assert.Equal(2, tags.RootElement.GetArrayLength());
    }

    [Fact]
    public void ReadObject_RejectsDuplicateFields()
    {
        using var document = JsonDocument.Parse("""{ "nombre": "Ana", "nombre": "Luis" }""");

        var exception = Assert.Throws<RuntimeQueryException>(
            () => RuntimeJsonValues.ReadObject(document.RootElement));

        Assert.Equal("INVALID_BODY", exception.Code);
    }

    [Fact]
    public void ReadObject_RejectsNonObjectBodies()
    {
        using var document = JsonDocument.Parse("[1, 2, 3]");

        var exception = Assert.Throws<RuntimeQueryException>(
            () => RuntimeJsonValues.ReadObject(document.RootElement));

        Assert.Equal("INVALID_BODY", exception.Code);
    }

    [Fact]
    public void ReadMany_AcceptsOneObjectOrAnArrayOfObjects()
    {
        using var single = JsonDocument.Parse("""{ "nombre": "Ana" }""");
        using var array = JsonDocument.Parse("""[{ "nombre": "Ana" }, { "nombre": "Luis" }]""");

        Assert.Single(RuntimeJsonValues.ReadMany(single.RootElement));
        Assert.Equal(2, RuntimeJsonValues.ReadMany(array.RootElement).Count);
    }

    [Fact]
    public void ReadMany_RejectsEmptyArrays()
    {
        using var document = JsonDocument.Parse("[]");

        var exception = Assert.Throws<RuntimeQueryException>(
            () => RuntimeJsonValues.ReadMany(document.RootElement));

        Assert.Equal("INVALID_BODY", exception.Code);
    }

    [Fact]
    public void ReadMany_RejectsArrayItemsThatAreNotObjects()
    {
        using var document = JsonDocument.Parse("""[{ "nombre": "Ana" }, 5]""");

        var exception = Assert.Throws<RuntimeQueryException>(
            () => RuntimeJsonValues.ReadMany(document.RootElement));

        Assert.Equal("INVALID_BODY", exception.Code);
    }
}
