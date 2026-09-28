using System.Text.RegularExpressions;

namespace PaperSubmissionManager.Services;

public static class RevisionCommentFormat
{
    // The deliberately simple tag format keeps review comments in their original language
    // without requiring the user to escape quotation marks or newlines as JSON would.
    public const string PromptTemplate = """
        请对下面的期刊返修邮件或审稿意见进行格式化处理。

        识别其中所有需要作者处理的独立审稿意见。

        每一条可以独立回复或处理的意见，分别使用：

        <comment>
        意见原文
        </comment>

        进行包裹。

        要求：
        1. 保留审稿意见原文，不总结、不翻译、不改写。
        2. 一个能够独立回复的问题作为一个 comment。
        3. 不输出 Reviewer 标题、Major Comments、Minor Comments 等标题。
        4. 不输出邮件称呼、感谢语、投稿编号、截止日期、邮件签名等无关内容。
        5. 不添加任何解释、编号或其他文字。
        6. 最终只输出 <comment>...</comment> 内容。

        原始文本如下：

        {{RAW_TEXT}}
        """;

    private static readonly Regex CommentPattern = new(
        @"<comment>(.*?)</comment>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public static string BuildPrompt(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) throw new ArgumentException("请先粘贴原始返修邮件或审稿意见。");
        return PromptTemplate.Replace("{{RAW_TEXT}}", rawText, StringComparison.Ordinal);
    }

    public static IReadOnlyList<string> Parse(string formattedText)
    {
        if (string.IsNullOrWhiteSpace(formattedText)) throw new ArgumentException("请先粘贴处理后的意见文本。");
        var comments = new List<string>();
        var position = 0;
        foreach (Match match in CommentPattern.Matches(formattedText))
        {
            if (!string.IsNullOrWhiteSpace(formattedText[position..match.Index]))
                throw new FormatException("意见文本只能包含 <comment>...</comment> 标签，请去除标签外的文字。");
            var comment = match.Groups[1].Value.Trim();
            if (comment.Length == 0) throw new FormatException("存在空的 <comment> 标签，请补全或删除。");
            comments.Add(comment);
            position = match.Index + match.Length;
            if (comments.Count > 10000) throw new FormatException("单次最多导入 10000 条意见。");
        }
        if (comments.Count == 0 || !string.IsNullOrWhiteSpace(formattedText[position..]))
            throw new FormatException("未找到完整的 <comment>...</comment> 意见，或末尾含有标签外文字。");
        return comments;
    }
}
