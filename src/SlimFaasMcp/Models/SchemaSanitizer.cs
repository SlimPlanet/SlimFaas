// SlimFaasMcp/Models/SchemaSanitizer.cs
using System.Collections;
using System.Text.RegularExpressions;

namespace SlimFaasMcp.Models;

public static class SchemaSanitizer
{
    // Mots-clés JSON Schema qu’on garde (subset largement compatible avec les clients MCP)
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
        // number/integer
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        // combinators (si tu les utilises/autorises côté client)
        "anyOf", "oneOf", "allOf", "not"
    };

    private static readonly Regex VendorExt = new(@"^x\-", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Clés OpenAPI/Swagger à supprimer systématiquement
    private static readonly HashSet<string> DropKeys = new(StringComparer.Ordinal)
    {
        "$ref",               // on a déjà expansé
        "nullable",           // OpenAPI 3.0 (utiliser anyOf [null, T] si besoin)
        "readOnly", "writeOnly",
        "deprecated",
        "xml", "discriminator", "externalDocs",
        "example", "examples",
        "requiredIf", "oneOfExclusive", // divers restes de convertisseurs
    };

    public static object SanitizeForMcp(object node)
    {
        switch (node)
        {
            case Dictionary<string, object> dict:
                return SanitizeDict(dict);

            case IList list:
            {
                var newList = new List<object>(list.Count);
                foreach (var item in list)
                    newList.Add(SanitizeForMcp(item));
                return newList;
            }

            default:
                return node; // primitive / JsonElement déjà aplati par ExpandSchema
        }
    }

    private static object SanitizeDict(Dictionary<string, object> dict)
    {
        // 1) Nettoie clés interdites et vendor-ext
        var keys = dict.Keys.ToList();
        foreach (var k in keys)
        {
            if (DropKeys.Contains(k) || VendorExt.IsMatch(k) || !AllowedKeys.Contains(k))
            {
                dict.Remove(k);
                continue;
            }

            // 2) Descend récursivement
            var v = dict[k];
            dict[k] = SanitizeForMcp(v);
        }

        // 3) Normalisations spécifiques

        // additionalProperties doit être bool ou schema
        if (dict.TryGetValue("additionalProperties", out var ap))
        {
            if (ap is not bool && ap is not Dictionary<string, object>)
            {
                // fallback MCP-friendly : autoriser des props libres
                dict["additionalProperties"] = true;
            }
        }

        // items: doit être objet ou tableau d’objets (on garde tel quel si déjà bon)
        if (dict.TryGetValue("items", out var items))
        {
            if (items is not Dictionary<string, object> && items is not IList)
            {
                // fallback sur un item permissif
                dict["items"] = new Dictionary<string, object> { ["type"] = "object" };
            }
        }

        // properties: s’assurer que c’est un dict<string, object>
        if (dict.TryGetValue("properties", out var props) && props is Dictionary<string, object> pDict)
        {
            // Rien de plus à faire ici, on a déjà sanitizé récursivement
        }

        // required: garder uniquement les noms existants dans properties (si présent)
        if (dict.TryGetValue("required", out var req) && req is IList reqList
            && dict.TryGetValue("properties", out var pr) && pr is Dictionary<string, object> props2)
        {
            var filtered = new List<object>();
            foreach (var r in reqList)
            {
                if (r is string s && props2.ContainsKey(s))
                    filtered.Add(s);
            }
            dict["required"] = filtered;
        }

        // combinators: nettoyer chaque branche
        foreach (var comb in new[] { "anyOf", "oneOf", "allOf", "not" })
        {
            if (!dict.TryGetValue(comb, out var cv)) continue;
            if (cv is IList arr)
            {
                var newArr = new List<object>(arr.Count);
                foreach (var item in arr)
                    newArr.Add(SanitizeForMcp(item));
                dict[comb] = newArr;
            }
            else if (cv is Dictionary<string, object> d)
            {
                dict[comb] = SanitizeForMcp(d);
            }
        }

        return dict;
    }
}
