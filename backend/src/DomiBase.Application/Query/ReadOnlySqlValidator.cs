using System.Text.RegularExpressions;

namespace DomiBase.Application.Query;

/// <summary>
/// Conservative lexer that only accepts one read-only statement. String literals, quoted
/// identifiers, dollar-quoted text and comments are blanked before keyword analysis, so the
/// check cannot be fooled by a write keyword appearing inside a literal. PostgreSQL enforces
/// the same restriction with <c>SET TRANSACTION READ ONLY</c>; this validator is the first,
/// friendliest line of defence.
/// </summary>
public static partial class ReadOnlySqlValidator
{
    public const int MaxSqlLength = 8000;

    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.Ordinal)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "DROP", "ALTER", "CREATE",
        "TRUNCATE", "GRANT", "REVOKE", "COPY", "EXECUTE", "EXEC", "CALL", "DO",
        "SET", "RESET", "VACUUM", "ANALYZE", "REFRESH", "LOCK", "UNLOCK", "COMMENT",
        "REINDEX", "CLUSTER", "DISCARD", "LISTEN", "UNLISTEN", "NOTIFY", "PREPARE",
        "DEALLOCATE", "BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE",
        "ABORT", "INTO", "TEMP", "TEMPORARY", "UNLOGGED", "RETURNING", "OVERRIDING"
    };

    private static readonly HashSet<string> ForbiddenFunctions = new(StringComparer.Ordinal)
    {
        "set_config", "pg_read_file", "pg_read_binary_file", "pg_ls_dir", "pg_stat_file",
        "lo_import", "lo_export", "dblink", "pg_terminate_backend", "pg_cancel_backend",
        "pg_create_restore_point", "pg_reload_conf", "pg_rotate_logfile",
        "nextval", "setval"
    };

    public static bool TryValidate(string? sql, out string statement, out string? error)
    {
        statement = string.Empty;

        if (string.IsNullOrWhiteSpace(sql))
        {
            error = "A SQL statement is required.";
            return false;
        }

        if (sql.Length > MaxSqlLength)
        {
            error = $"The SQL statement cannot exceed {MaxSqlLength} characters.";
            return false;
        }

        if (!TryStripLiteralsAndComments(sql, out var code, out error))
        {
            return false;
        }

        var separator = code.IndexOf(';');
        if (separator >= 0)
        {
            if (code.IndexOf(';', separator + 1) >= 0
                || code.AsSpan(separator + 1).Trim().Length != 0)
            {
                error = "Only one SQL statement is allowed.";
                return false;
            }
        }

        var end = separator >= 0 ? separator : sql.Length;
        statement = sql[..end].Trim();
        var body = code[..end];

        var words = WordPattern().Matches(body);
        if (words.Count == 0)
        {
            error = "A SQL statement is required.";
            return false;
        }

        var first = words[0].Value.ToUpperInvariant();
        if (first is not ("SELECT" or "WITH"))
        {
            error = "Only SELECT statements (optionally starting with WITH) are allowed.";
            return false;
        }

        foreach (Match word in words)
        {
            var value = word.Value;
            if (ForbiddenKeywords.Contains(value.ToUpperInvariant())
                || ForbiddenFunctions.Contains(value))
            {
                error = $"The statement contains a disallowed operation ('{value}').";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool TryStripLiteralsAndComments(
        string sql,
        out string code,
        out string? error)
    {
        var chars = sql.ToCharArray();
        var index = 0;

        while (index < chars.Length)
        {
            var current = chars[index];

            if (current == '\'')
            {
                // PostgreSQL string literal. Backslash is consumed defensively as an escape.
                BlankUntilClosing(chars, ref index, '\'', consumeBackslash: true);
            }
            else if (current == '"')
            {
                BlankUntilClosing(chars, ref index, '"', consumeBackslash: false);
            }
            else if (current == '-' && index + 1 < chars.Length && chars[index + 1] == '-')
            {
                while (index < chars.Length && chars[index] != '\n')
                {
                    chars[index++] = ' ';
                }
            }
            else if (current == '/' && index + 1 < chars.Length && chars[index + 1] == '*')
            {
                var depth = 1;
                chars[index++] = ' ';
                chars[index++] = ' ';
                while (index < chars.Length && depth > 0)
                {
                    if (chars[index] == '/' && index + 1 < chars.Length && chars[index + 1] == '*')
                    {
                        chars[index++] = ' ';
                        chars[index++] = ' ';
                        depth++;
                    }
                    else if (chars[index] == '*' && index + 1 < chars.Length && chars[index + 1] == '/')
                    {
                        chars[index++] = ' ';
                        chars[index++] = ' ';
                        depth--;
                    }
                    else
                    {
                        chars[index++] = ' ';
                    }
                }

                if (depth != 0)
                {
                    code = string.Empty;
                    error = "The SQL statement has an unterminated block comment.";
                    return false;
                }
            }
            else if (current == '$' && TryDollarQuoteLength(sql, index, out var delimiterLength))
            {
                var delimiter = sql[index..(index + delimiterLength)];
                var closing = sql.IndexOf(delimiter, index + delimiterLength, StringComparison.Ordinal);
                if (closing < 0)
                {
                    code = string.Empty;
                    error = "The SQL statement has an unterminated dollar-quoted string.";
                    return false;
                }

                var end = closing + delimiterLength;
                for (var position = index; position < end; position++)
                {
                    chars[position] = ' ';
                }

                index = end;
            }
            else
            {
                index++;
            }
        }

        code = new string(chars);
        error = null;
        return true;
    }

    private static void BlankUntilClosing(char[] chars, ref int index, char quote, bool consumeBackslash)
    {
        chars[index++] = ' ';
        while (index < chars.Length)
        {
            if (consumeBackslash && chars[index] == '\\' && index + 1 < chars.Length)
            {
                chars[index++] = ' ';
                chars[index++] = ' ';
                continue;
            }

            if (chars[index] == quote)
            {
                chars[index++] = ' ';
                if (index < chars.Length && chars[index] == quote)
                {
                    chars[index++] = ' ';
                    continue;
                }

                return;
            }

            chars[index++] = ' ';
        }
    }

    private static bool TryDollarQuoteLength(string sql, int start, out int length)
    {
        length = 0;
        if (start + 1 >= sql.Length || sql[start] != '$')
        {
            return false;
        }

        if (sql[start + 1] == '$')
        {
            length = 2;
            return true;
        }

        var position = start + 1;
        if (!IsTagStart(sql[position]))
        {
            return false;
        }

        position++;
        while (position < sql.Length && IsTagPart(sql[position]))
        {
            position++;
        }

        if (position < sql.Length && sql[position] == '$')
        {
            length = position - start + 1;
            return true;
        }

        return false;
    }

    private static bool IsTagStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsTagPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_$]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
