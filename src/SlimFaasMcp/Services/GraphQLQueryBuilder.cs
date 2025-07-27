using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

public static class GraphQLQueryBuilder
{
    /* ════════════════════════════════════════════════════════════════
     *  Helpers
     * ═════════════════════════════════════════════════════════════ */

    private static JsonElement Unwrap(JsonElement node)
    {
        while (node.ValueKind == JsonValueKind.Object &&
               node.TryGetProperty("kind", out var k) &&
               (k.GetString() is "NON_NULL" or "LIST") &&
               node.TryGetProperty("ofType", out var inner) &&
               inner.ValueKind == JsonValueKind.Object)
        {
            node = inner;
        }
        return node;
    }

    /// <summary>
    /// Retourne le <c>JsonElement</c> décrivant un type nommé <paramref name="name"/>.
    /// </summary>
    private static bool TryFindTypeByName(JsonElement schemaRoot, string name, out JsonElement type)
    {
        if (schemaRoot.TryGetProperty("types", out var typesArr) && typesArr.ValueKind == JsonValueKind.Array)
        {
            type = typesArr.EnumerateArray()
                           .FirstOrDefault(t =>
                               t.TryGetProperty("name", out var n) &&
                               n.GetString() == name);
            return type.ValueKind != JsonValueKind.Undefined;
        }

        type = default;
        return false;
    }

    private static string BuildSelection(JsonElement typeNode,
                                         JsonElement schemaRoot,
                                         HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();

        // Accepte soit :
        //   • le nœud « type » classique (kind/name/ofType…)
        //   • le nœud d’un type complet (OBJECT, SCALAR…) déjà issu de schema.types
        var candidate = typeNode;

        // Cas 1 : nœud classique → unwrap
        if (candidate.TryGetProperty("kind", out _))
            candidate = Unwrap(candidate);

        // Cas 2 : pas de "kind" mais on est déjà sur un type complet (as-is)

        if (!candidate.TryGetProperty("kind", out var kindProp))
            return string.Empty;

        var kind = kindProp.GetString();
        if (kind is "SCALAR" or "ENUM" or null)
            return string.Empty;

        if (!candidate.TryGetProperty("name", out var nameProp))
            return string.Empty;

        string typeName = nameProp.GetString() ?? string.Empty;
        if (typeName.Length == 0 || !visited.Add(typeName))
            return string.Empty;                         // boucle ou nom absent

        // S’assurer de disposer d’un objet complet (avec "fields")
        if (candidate.ValueKind != JsonValueKind.Object ||
            !candidate.TryGetProperty("fields", out var fieldsArr) ||
            fieldsArr.ValueKind != JsonValueKind.Array)
        {
            // Peut‑être qu’on n’a qu’une référence → aller chercher dans types
            if (!TryFindTypeByName(schemaRoot, typeName, out var complete) ||
                !complete.TryGetProperty("fields", out fieldsArr) ||
                fieldsArr.ValueKind != JsonValueKind.Array)
                return "{ __typename }";                // impossible → fallback
        }

        var sb = new StringBuilder("{ ");
        foreach (var field in fieldsArr.EnumerateArray())
        {
            if (!field.TryGetProperty("name", out var fnProp) ||
                fnProp.GetString() is not { Length: >0 } fieldName)
                continue;

            sb.Append(fieldName);

            // Descente récursive uniquement si on connaît le type du sous‑champ
            if (field.TryGetProperty("type", out var subType))
            {
                string subSel = BuildSelection(subType, schemaRoot, visited);
                if (!string.IsNullOrEmpty(subSel))
                    sb.Append(' ').Append(subSel);
            }

            sb.Append(' ');
        }

        if (sb.Length <= 2) sb.Append("__typename ");   // sécurité mini
        sb.Append('}');
        return sb.ToString();
    }

    /* ════════════════════════════════════════════════════════════════
     *  Public API
     * ═════════════════════════════════════════════════════════════ */

    public static string BuildQuery(string toolName,
                                    IReadOnlyDictionary<string, JsonElement> variables,
                                    JsonDocument schema)
    {
        bool   isMutation = toolName.StartsWith("mutation_", StringComparison.Ordinal);
        string opKind     = isMutation ? "mutation" : "query";
        string fieldName  = toolName[(opKind.Length + 1)..];       // après « query_ » / « mutation_ »

        var root = schema.RootElement.GetProperty("data").GetProperty("__schema");

        /* ---------- TYPE RACINE (queryType / mutationType) ---------- */

        if (!root.TryGetProperty(isMutation ? "mutationType" : "queryType", out var opElem) ||
            opElem.ValueKind != JsonValueKind.Object ||
            !opElem.TryGetProperty("name", out var opNameProp))
            return $"{opKind} {{ {fieldName} }}";

        string opTypeName = opNameProp.GetString() ?? string.Empty;

        if (!TryFindTypeByName(root, opTypeName, out var opType))
            return $"{opKind} {{ {fieldName} }}";

        /* -------------------- CHAMP RACINE ------------------------- */

        var field = opType.GetProperty("fields").EnumerateArray()
                          .FirstOrDefault(f =>
                              f.TryGetProperty("name", out var n) &&
                              string.Equals(n.GetString(), fieldName, StringComparison.OrdinalIgnoreCase));

        if (field.ValueKind == JsonValueKind.Undefined)
            return $"{opKind} {{ {fieldName} }}";

        /* --------------- VARIABLES & ARGUMENTS --------------------- */

        var defList = new List<string>();
        var argList = new List<string>();

        if (field.TryGetProperty("args", out var argsArr) && argsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in argsArr.EnumerateArray())
            {
                if (!arg.TryGetProperty("name", out var aNameProp))
                    continue;

                string argName = aNameProp.GetString() ?? string.Empty;
                if (argName.Length == 0) continue;

                string gqlType;
                bool   nonNull = false;

                // ⇒ Cas idéal : présente dans le schéma
                if (arg.TryGetProperty("type", out var aTypeProp))
                {
                    var unwrapped = Unwrap(aTypeProp);
                    gqlType = unwrapped.TryGetProperty("name", out var tn) &&
                              tn.GetString() is { Length: >0 } s ? s : "String";
                    nonNull = aTypeProp.TryGetProperty("kind", out var k) &&
                              k.GetString() == "NON_NULL";
                }
                else
                {
                    // ⇒ Métadonnées manquantes : heuristique
                    gqlType = argName.Equals("code", StringComparison.OrdinalIgnoreCase) ||
                              argName.EndsWith("id", StringComparison.OrdinalIgnoreCase)
                              ? "ID"
                              : "String";
                }

                defList.Add($"${argName}: {gqlType}{(nonNull ? "!" : "")}");
                argList.Add($"{argName}: ${argName}");
            }
        }

        // Aucun argument dans le schéma → mais des variables fournies par l’appelant ?
        foreach (var kvp in variables)
        {
            if (defList.Any(d => d.StartsWith($"${kvp.Key}:"))) continue; // déjà ajouté
            string guessedType = kvp.Value.ValueKind switch
            {
                JsonValueKind.Number => "Int",
                JsonValueKind.True or JsonValueKind.False => "Boolean",
                _ => "String"
            };
            defList.Add($"${kvp.Key}: {guessedType}");
            argList.Add($"{kvp.Key}: ${kvp.Key}");
        }

        string headerPart = defList.Count > 0 ? $"({string.Join(", ", defList)})" : string.Empty;
        string argsPart   = argList.Count > 0 ? $"({string.Join(", ", argList)})" : string.Empty;

        /* ------------------- SÉLECTION RETOUR ---------------------- */

        string selection = "{ __typename }";                // valeur sûre
        JsonElement fieldType;

        if (field.TryGetProperty("type", out fieldType) ||
            // metadata manquante → on devine le type « PascalCase(fieldName) »
            TryFindTypeByName(root,
                char.ToUpper(fieldName[0]) + fieldName[1..], out fieldType))
        {
            string sel = BuildSelection(fieldType, root);
            if (!string.IsNullOrWhiteSpace(sel))
                selection = sel;
        }

        /* -------------------- REQUÊTE FINALE ----------------------- */

        string opName = $"{char.ToUpper(fieldName[0])}{fieldName[1..]}Op";

        var sb = new StringBuilder();
        sb.Append(opKind).Append(' ').Append(opName);
        if (headerPart.Length > 0) sb.Append(' ').Append(headerPart);
        sb.Append(" { ").Append(fieldName).Append(argsPart).Append(' ').Append(selection).Append(" }");

        return sb.ToString();
    }
}
