// SlimFaasMcp/Models/SchemaSanitizer.cs
using System.Collections;
using System.Text.RegularExpressions;

namespace SlimFaasMcp.Models;

public static class SchemaSanitizer
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        // core
        "$schema", "$id", "type", "enum", "const", "default", "description", "title",
        // object
        "properties", "required", "additionalProperties", "minProperties", "maxProperties",
        // array
        "items", "minItems", "maxItems", "uniqueItems",
        // string
        "minLength", "maxLength", "pattern", "format",
        // numeric
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        // combinators
        "anyOf", "oneOf", "allOf", "not"
    };

    private static readonly Regex VendorExt = new(@"^x\-", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> DropKeys = new(StringComparer.Ordinal)
    {
        "$ref",
        "nullable",
        "readOnly", "writeOnly",
        "deprecated",
        "xml", "discriminator", "externalDocs",
        "example", "examples",
        // divers convertisseurs exotiques éventuels
        "requiredIf", "oneOfExclusive",
    };

    // Clés dont la valeur est une "map de schémas" : NE PAS filtrer les noms de propriété ici
    private static readonly HashSet<string> SchemaMapContainers = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "$defs", "definitions", "schemas" // "schemas" pour components.schemas (voir path)
    };

    public static object SanitizeForMcp(object node) => Sanitize(node, parentKey: null, path: Array.Empty<string>());

    private static object Sanitize(object node, string? parentKey, IReadOnlyList<string> path)
    {
        switch (node)
        {
            case Dictionary<string, object> dict:
                return SanitizeDict(dict, parentKey, path);

            case IList list:
            {
                var newList = new List<object>(list.Count);
                foreach (var item in list)
                    newList.Add(Sanitize(item, parentKey: null, path));
                return newList;
            }

            default:
                return node; // primitives etc.
        }
    }

    private static object SanitizeDict(Dictionary<string, object> dict, string? parentKey, IReadOnlyList<string> path)
    {
        // Sommes-nous dans une "map de schémas" (ex: à l'intérieur de "properties") ?
        var inSchemaMap =
            (parentKey != null && SchemaMapContainers.Contains(parentKey)) ||
            // Cas spécial components.schemas : path se termine par ["components","schemas"]
            (path.Count >= 2 && path[^2] == "components" && path[^1] == "schemas");

        if (inSchemaMap)
        {
            // Ne PAS filtrer les noms : ce sont des noms de champs
            var keys = dict.Keys.ToList();
            foreach (var propName in keys)
            {
                dict[propName] = Sanitize(dict[propName], parentKey: null, Combine(path, propName));
            }
            return dict;
        }

        // Niveau "schéma" : on filtre selon allow-list et drop-list
        var toProcess = dict.Keys.ToList();
        foreach (var k in toProcess)
        {
            if (DropKeys.Contains(k) || VendorExt.IsMatch(k) || !AllowedKeys.Contains(k))
            {
                dict.Remove(k);
                continue;
            }

            dict[k] = Sanitize(dict[k], parentKey: k, Combine(path, k));
        }

        // Normalisations

        // additionalProperties : doit être bool ou dict
        if (dict.TryGetValue("additionalProperties", out var ap) &&
            ap is not bool && ap is not Dictionary<string, object>)
        {
            dict["additionalProperties"] = true;
        }

        // items : doit être dict ou liste de dicts
        if (dict.TryGetValue("items", out var items))
        {
            if (items is not Dictionary<string, object> && items is not IList)
            {
                dict["items"] = new Dictionary<string, object> { ["type"] = "object" };
            }
        }

        // required : garder seulement les clés existant dans properties
        if (dict.TryGetValue("required", out var req) && req is IList reqList &&
            dict.TryGetValue("properties", out var props) && props is Dictionary<string, object> propsDict)
        {
            var filtered = new List<object>();
            foreach (var r in reqList)
            {
                if (r is string s && propsDict.ContainsKey(s))
                    filtered.Add(s);
            }
            dict["required"] = filtered;
        }

        // Si l'objet a "properties" mais que c'est vide, autoriser des props libres (optionnel)
        if (dict.TryGetValue("type", out var t) && t is string ts && ts == "object")
        {
            if (dict.TryGetValue("properties", out var p) && p is Dictionary<string, object> pd && pd.Count == 0)
            {
                // Ne *pas* forcer additionalProperties=true si tu veux rester strict.
                // Ici, on n'ajoute rien pour ne pas masquer un vrai problème amont.
            }
        }

        return dict;
    }

    private static IReadOnlyList<string> Combine(IReadOnlyList<string> prefix, string next)
    {
        var arr = new string[prefix.Count + 1];
        for (int i = 0; i < prefix.Count; i++) arr[i] = prefix[i];
        arr[^1] = next;
        return arr;
    }
}
