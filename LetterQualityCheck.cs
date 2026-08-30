namespace OliviaLetterOverlay;

// 回信质量硬校验 + 一次修复重写（借鉴 linli-local-mail 的 validate/repair 思路，
// 违规清单按本项目的文风规则定制）。修复失败时保留原稿，不让用户干等。
internal static class LetterQualityCheck
{
    public static readonly string[] BannedPhrases =
    [
        "我理解你", "别想太多", "你真的很棒", "相信自己", "一切都会好的",
        "加油", "总而言之", "愿你", "在这个",
    ];

    private static readonly string[] HighPenaltyTemplatePhrases =
    [
        "收到你的信了", "谢谢你愿意", "无论怎样", "不管怎样", "你不是一个人", "希望你能",
    ];

    private static readonly string[] HighPenaltyUnnaturalPhrases =
    [
        "你说的对", "你说得对", "确实如此", "我同意你的看法", "从你的描述中", "我能够感受到", "我能感受到",
        "本质上", "某种程度上", "值得被", "允许自己", "情绪价值", "保持觉察", "换个角度", "归根结底", "不得不说",
    ];

    private static readonly string[] EmotionalResponseMarkers =
    [
        "在意", "担心", "心里", "听到", "看见", "替你", "陪你", "高兴", "开心", "难受", "委屈", "不容易", "辛苦", "放心", "舍不得",
    ];

    public static List<string> Validate(string? text, string signatureName, bool requireEmotion = false)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            issues.Add("正文为空");
            return issues;
        }

        if (text.Contains('*'))
        {
            issues.Add("出现了星号动作标记（角色扮演舞台指示），要删除");
        }

        if (text.Contains('“') || text.Contains('”') || text.Contains('「') || text.Contains('」') || text.Contains('"'))
        {
            issues.Add("出现了引号，引用要改成直接转述");
        }

        foreach (var phrase in BannedPhrases)
        {
            if (text.Contains(phrase, StringComparison.Ordinal))
            {
                issues.Add("出现了套话「" + phrase + "」，要删除或改写成自己的话");
            }
        }

        foreach (var phrase in HighPenaltyTemplatePhrases)
        {
            if (text.Contains(phrase, StringComparison.Ordinal))
            {
                issues.Add("严重模板化：出现了万能表达「" + phrase + "」，必须换成只针对这封来信的具体回应");
            }
        }

        foreach (var phrase in HighPenaltyUnnaturalPhrases)
        {
            if (text.Contains(phrase, StringComparison.Ordinal))
            {
                issues.Add("严重不自然表达：出现了日常聊天很少这样说的话「" + phrase + "」，必须换成普通人会说的常用说法");
            }
        }

        if (requireEmotion && !EmotionalResponseMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
        {
            issues.Add("严重情绪缺失：来信有明显情绪时，不能只回答事情本身；要写出具体的在意或感受");
        }

        var signature = "—— " + signatureName;
        var signatureIndex = text.LastIndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
        {
            issues.Add("缺少「" + signature + "」落款");
        }
        else
        {
            var nameIndex = text.IndexOf(signatureName, StringComparison.Ordinal);
            if (nameIndex >= 0 && nameIndex < signatureIndex)
            {
                issues.Add("正文中途出现了角色名，角色名只能出现在结尾落款");
            }
        }

        if (text.Length > 1600)
        {
            issues.Add("太长了，要删减到 1600 字以内");
        }

        return issues;
    }

    public static List<object> BuildRepairMessages(List<object> messages, string draft, List<string> issues)
    {
        var hasHighPenaltyTemplate = issues.Any(issue => issue.StartsWith("严重模板化", StringComparison.Ordinal));
        var hasMissingEmotion = issues.Any(issue => issue.StartsWith("严重情绪缺失", StringComparison.Ordinal));
        var hasUnnaturalWording = issues.Any(issue => issue.StartsWith("严重不自然表达", StringComparison.Ordinal));
        var repaired = new List<object>(messages)
        {
            new { role = "assistant", content = draft },
            new
            {
                role = "user",
                content = "你上一封回信存在以下问题：\n- " + string.Join("\n- ", issues)
                    + (hasHighPenaltyTemplate
                        ? "\n\n其中“严重模板化”优先级最高：必须彻底换成只属于这封来信的具体回应，不能保留原句、近义改写或相同三段式。宁可简短，也不要写万能安慰。"
                        : string.Empty)
                    + (hasMissingEmotion
                        ? "\n\n其中“严重情绪缺失”优先级最高：先补上一句只针对这封来信的真实在意或感受，再回答事情本身；不能只给建议、判断或解决方案。"
                        : string.Empty)
                    + (hasUnnaturalWording
                        ? "\n\n其中“严重不自然表达”优先级最高：整句换成现实里普通人会说的常用话，不要只替换一两个词，也不要换成另一句书面腔或心理咨询腔。"
                        : string.Empty)
                    + "\n\n请重写一封完整的回信：只修正上面列出的问题，其余内容尽量原样保留，不要延长信件；只输出回信正文本身。",
            },
        };
        return repaired;
    }

    public static bool IsRepairImproved(IReadOnlyList<string> originalIssues, IReadOnlyList<string> remainingIssues)
    {
        var originalHighPenaltyCount = originalIssues.Count(IsHighPenaltyIssue);
        var remainingHighPenaltyCount = remainingIssues.Count(IsHighPenaltyIssue);
        return remainingHighPenaltyCount < originalHighPenaltyCount
            || originalHighPenaltyCount == 0 && remainingIssues.Count <= originalIssues.Count;
    }

    private static bool IsHighPenaltyIssue(string issue) => issue.StartsWith("严重模板化", StringComparison.Ordinal)
        || issue.StartsWith("严重情绪缺失", StringComparison.Ordinal)
        || issue.StartsWith("严重不自然表达", StringComparison.Ordinal);
}
