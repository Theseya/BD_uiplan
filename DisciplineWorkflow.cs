namespace WebApplication1;

/// <summary>Статусы согласования дисциплины между АР (ОП) и департаментом.</summary>
public static class DisciplineWorkflow
{
    public const string Draft = "draft";
    public const string Sent = "sent";
    public const string UnderReview = "under_review";
    public const string Rejected = "rejected";
    public const string UnderCorrection = "under_correction";
    public const string Approved = "approved";

    public static bool IsPublished(string? status, string? smartplanId) =>
        string.Equals(status?.Trim(), Approved, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(smartplanId);

    /// <summary>Дисциплина согласована департаментом (нельзя править учебный план / каталог без процедуры доработки).</summary>
    public static bool IsApprovedStatus(string? status) =>
        string.Equals(status?.Trim(), Approved, StringComparison.OrdinalIgnoreCase);

    public static bool OpMayEditRow(string? status)
    {
        var s = status?.Trim() ?? "";
        return s.Length == 0
            || string.Equals(s, Draft, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, Rejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, UnderCorrection, StringComparison.OrdinalIgnoreCase);
    }

    public static bool OpMayDeleteRow(string? status) => OpMayEditRow(status);

    public static string StatusLabelRu(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        Draft => "Черновик",
        Sent => "На согласовании",
        UnderReview => "На рассмотрении",
        Rejected => "Отклонено",
        UnderCorrection => "На корректировке",
        Approved => "Согласовано",
        _ => status ?? "—"
    };
}
