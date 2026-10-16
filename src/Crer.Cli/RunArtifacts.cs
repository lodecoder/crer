using System.Globalization;
using System.Text;

namespace Crer;

public static class RunArtifacts
{
    public static string CreateRunId(string name, DateTimeOffset? now = null, Guid? id = null)
    {
        var safe = new StringBuilder();
        var inInvalid = false;
        foreach (var rune in name.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var valid = Rune.IsLetter(rune) || category is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber || rune.Value is '_' or '-';
            if (valid) { safe.Append(rune); inInvalid = false; }
            else if (!inInvalid) { safe.Append('-'); inInvalid = true; }
        }
        var trimmed = safe.ToString().Trim('-');
        trimmed = string.Concat(trimmed.EnumerateRunes().Take(64).Select(rune => rune.ToString())).TrimEnd('-');
        if (trimmed.Length == 0) trimmed = "run";
        return $"{(now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)}-{trimmed}-{id ?? Guid.NewGuid()}";
    }
}
