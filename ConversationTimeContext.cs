namespace OliviaLetterOverlay;

internal static class ConversationTimeContext
{
    public static DateTime? LastUserLetterAt(IReadOnlyList<SavedLetter> history) => history
        .Where(letter => !letter.IsAutoLetter && !letter.IsReference && !string.IsNullOrWhiteSpace(letter.Draft))
        .Select(letter => (DateTime?)letter.CreatedAt)
        .Max();

    public static string BuildPromptContext(IReadOnlyList<SavedLetter> history, DateTime now)
    {
        var weekday = new[] { "日", "一", "二", "三", "四", "五", "六" }[(int)now.DayOfWeek];
        var lastUserAt = LastUserLetterAt(history);
        var lastAutomaticAt = history
            .Where(letter => letter.IsAutoLetter)
            .Select(letter => (DateTime?)letter.CreatedAt)
            .Max();

        var userLine = lastUserAt is null
            ? "用户还没有写过信。"
            : $"用户上次写信是 {lastUserAt:yyyy-MM-dd HH:mm}，距今 {DescribeElapsed(now - lastUserAt.Value)}。";
        var automaticLine = lastAutomaticAt is null
            ? "你还没有主动寄过信。"
            : $"你上次主动寄信是 {lastAutomaticAt:yyyy-MM-dd HH:mm}，距今 {DescribeElapsed(now - lastAutomaticAt.Value)}。";
        return $"时间背景：当前本地时间是 {now:yyyy-MM-dd HH:mm}，星期{weekday}。{userLine}{automaticLine}时间只用来帮助你判断分寸，不要生硬报时，也不要假装看见用户现实中的事。";
    }

    private static string DescribeElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) return "刚刚";
        if (elapsed.TotalDays >= 1) return $"{(int)elapsed.TotalDays}天{elapsed.Hours}小时";
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}小时{elapsed.Minutes}分钟";
        return elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}分钟" : "刚刚";
    }
}
