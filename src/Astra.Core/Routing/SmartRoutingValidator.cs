namespace Astra.Core.Routing;

/// <summary>
/// A reason smart-routing settings cannot be saved or enabled: a stable <paramref name="Code"/> the admin UI turns into
/// its own words, an English <paramref name="Message"/> for everything else, and the form field it is about (camelCase
/// path such as <c>grades[1].model</c>).
/// </summary>
public sealed record RoutingProblem(string Code, string Message, string? Field = null);

/// <summary>
/// The rules smart-routing settings must satisfy (plan §5.7). <see cref="Check"/> is the shape every save must have —
/// a half-finished setup can be saved while it is off — and <see cref="CheckComplete"/> is what turning it on needs on
/// top. Whether each grade's model is one a provider serves is the service's check: it needs the database.
/// </summary>
public static class SmartRoutingValidator
{
    public const int MinGrades = 2;
    public const int MaxGrades = 6;

    public static class Codes
    {
        public const string VirtualModelInvalid = "virtual_model_invalid";
        public const string VirtualModelConflict = "virtual_model_conflict";
        public const string JudgeProtocolInvalid = "judge_protocol_invalid";
        public const string JudgeUrlInvalid = "judge_url_invalid";
        public const string JudgeKeyInvalid = "judge_key_invalid";
        public const string JudgeKeyMissing = "judge_key_missing";
        public const string JudgeModelMissing = "judge_model_missing";
        public const string OutOfRange = "out_of_range";
        public const string TooLong = "too_long";
        public const string GradesCount = "grades_count";
        public const string GradeIdInvalid = "grade_id_invalid";
        public const string GradeIdDuplicate = "grade_id_duplicate";
        public const string GradeDescriptionMissing = "grade_description_missing";
        public const string GradeModelMissing = "grade_model_missing";
        public const string GradeModelUnroutable = "grade_model_unroutable";
        public const string FallbackGradeUnknown = "fallback_grade_unknown";
        public const string ModeInvalid = "mode_invalid";
    }

    /// <summary>The shape rules; an empty list means the settings can be stored.</summary>
    public static List<RoutingProblem> Check(SmartRoutingSettings s)
    {
        var problems = new List<RoutingProblem>();
        if (!IsValidVirtualModel(s.VirtualModel))
            problems.Add(new(Codes.VirtualModelInvalid,
                "virtualModel must start with a letter or digit and use only letters, digits, '.', '_' and '-' (at most 64 characters)", "virtualModel"));

        var judge = s.Judge;
        if (!JudgeProtocols.All.Contains(judge.Protocol))
            problems.Add(new(Codes.JudgeProtocolInvalid, $"judge.protocol must be one of: {string.Join(", ", JudgeProtocols.All)}", "judge.protocol"));
        if (judge.BaseUrl is not null && !IsValidBaseUrl(judge.BaseUrl))
            problems.Add(new(Codes.JudgeUrlInvalid, "judge.baseUrl must be an http(s) address without credentials, query or fragment", "judge.baseUrl"));
        if (judge.Model.Length > 200) problems.Add(new(Codes.TooLong, "judge.model is too long (at most 200 characters)", "judge.model"));
        if (judge.TimeoutMs is < 200 or > 15000)
            problems.Add(new(Codes.OutOfRange, "judge.timeoutMs must be between 200 and 15000", "judge.timeoutMs"));
        if (judge.Instructions is { Length: > 2000 })
            problems.Add(new(Codes.TooLong, "judge.instructions is too long (at most 2000 characters)", "judge.instructions"));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (s.Grades.Count is < MinGrades or > MaxGrades)
            problems.Add(new(Codes.GradesCount, $"there must be {MinGrades} to {MaxGrades} grades", "grades"));
        for (var i = 0; i < s.Grades.Count; i++)
        {
            var grade = s.Grades[i];
            var at = $"grades[{i}]";
            if (!IsValidGradeId(grade.Id))
                problems.Add(new(Codes.GradeIdInvalid, "a grade id is 1 to 32 characters of a-z, 0-9, '_' and '-'", $"{at}.id"));
            else if (!ids.Add(grade.Id))
                problems.Add(new(Codes.GradeIdDuplicate, $"grade id '{grade.Id}' is used twice", $"{at}.id"));
            if (grade.Name.Length > 40) problems.Add(new(Codes.TooLong, "a grade name is at most 40 characters", $"{at}.name"));
            if (string.IsNullOrWhiteSpace(grade.Description))
                problems.Add(new(Codes.GradeDescriptionMissing, "a grade needs a description: it is what the judge reads", $"{at}.description"));
            else if (grade.Description.Length > 400)
                problems.Add(new(Codes.TooLong, "a grade description is at most 400 characters", $"{at}.description"));
            if (grade.Model.Length > 200) problems.Add(new(Codes.TooLong, "a grade model is at most 200 characters", $"{at}.model"));
            if (grade.ProviderId is { Length: > 200 }) problems.Add(new(Codes.TooLong, "a grade provider id is at most 200 characters", $"{at}.providerId"));
        }

        var rules = s.Rules;
        if (!RoutingModes.All.Contains(rules.Mode))
            problems.Add(new(Codes.ModeInvalid, $"rules.mode must be one of: {string.Join(", ", RoutingModes.All)}", "rules.mode"));
        if (rules.SessionTtlMinutes is < 1 or > 1440)
            problems.Add(new(Codes.OutOfRange, "rules.sessionTtlMinutes must be between 1 and 1440", "rules.sessionTtlMinutes"));
        if (double.IsNaN(rules.MinConfidence) || rules.MinConfidence is < 0 or > 1)
            problems.Add(new(Codes.OutOfRange, "rules.minConfidence must be between 0 and 1", "rules.minConfidence"));
        if (rules.ContextTurns is < 0 or > 5)
            problems.Add(new(Codes.OutOfRange, "rules.contextTurns must be between 0 and 5", "rules.contextTurns"));
        if (rules.MaxChars is < 500 or > 20000)
            problems.Add(new(Codes.OutOfRange, "rules.maxChars must be between 500 and 20000", "rules.maxChars"));
        if (!ids.Contains(rules.FallbackGrade))
            problems.Add(new(Codes.FallbackGradeUnknown, "rules.fallbackGrade must be the id of one of the grades", "rules.fallbackGrade"));
        return problems;
    }

    /// <summary>What turning smart routing on needs besides <see cref="Check"/>: a judge that can be called and a model for every grade.</summary>
    public static List<RoutingProblem> CheckComplete(SmartRoutingSettings s)
    {
        var problems = new List<RoutingProblem>();
        if (string.IsNullOrEmpty(s.Judge.ApiKeyProtected))
            problems.Add(new(Codes.JudgeKeyMissing, "the judge needs an API key", "judge.apiKey"));
        if (string.IsNullOrWhiteSpace(s.Judge.Model))
            problems.Add(new(Codes.JudgeModelMissing, "the judge needs a model", "judge.model"));
        for (var i = 0; i < s.Grades.Count; i++)
            if (string.IsNullOrWhiteSpace(s.Grades[i].Model))
                problems.Add(new(Codes.GradeModelMissing, $"grade '{s.Grades[i].Id}' needs a model", $"grades[{i}].model"));
        return problems;
    }

    public static bool IsValidGradeId(string id) =>
        id.Length is >= 1 and <= 32 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>
    /// Letters, digits, '.', '_' and '-' only: the id travels in URL paths (Gemini's <c>models/{id}:generateContent</c>)
    /// and in client configuration files.
    /// </summary>
    public static bool IsValidVirtualModel(string id) =>
        id.Length is >= 1 and <= 64
        && char.IsAsciiLetterOrDigit(id[0])
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static bool IsValidBaseUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.Host.Length > 0
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    /// <summary>
    /// Visible ASCII only, like TypeSafe's own SDK demands: the key becomes an HTTP header value, where a stray space,
    /// control character or non-ASCII letter (a pasted full-width character) breaks the request or splits the header.
    /// </summary>
    public static bool IsValidApiKey(string key) => key.Length is >= 1 and <= 512 && key.All(c => c is > ' ' and < (char)127);
}
