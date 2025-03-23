using System.Text;

namespace SlimFaas.Kubernetes;

public static class JsonMinifier
{
    public static string? MinifyJson(string? input)
    {
        if (input == null) return null;

        StringBuilder sb = new();
        bool inString = false;
        bool escapeNext = false;

        foreach (char c in input)
        {
            if (inString)
            {
                // On est dans une chaîne de caractères, on recopie tout
                sb.Append(c);

                if (escapeNext)
                {
                    // Le caractère précédent était un '\\',
                    // donc on ne change pas l'état inString
                    // mais on arrête juste de se dire "en échappement"
                    escapeNext = false;
                }
                else
                {
                    // Si on tombe sur un backslash,
                    // le prochain caractère est échappé
                    if (c == '\\')
                    {
                        escapeNext = true;
                    }
                    // Sinon, si c == '"', on quitte la chaîne
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
            }
            else
            {
                // On est hors chaîne de caractères
                if (char.IsWhiteSpace(c))
                {
                    // On ignore les whitespace hors chaîne
                    continue;
                }

                sb.Append(c);
                // Si on rencontre un guillemet, on entre dans une chaîne
                if (c == '"')
                {
                    inString = true;
                    escapeNext = false;
                }
            }
        }

        return sb.ToString();
    }
}
