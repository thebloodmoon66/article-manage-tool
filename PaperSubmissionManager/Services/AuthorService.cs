using System.Net.Mail;
using Microsoft.Data.Sqlite;
using PaperSubmissionManager.Models;

namespace PaperSubmissionManager.Services;

public sealed class AuthorService(DatabaseService database)
{
    public List<AuthorRecord> ListAuthors()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Id, a.Name, a.CreatedAt, COUNT(e.Id)
            FROM Authors a
            LEFT JOIN AuthorEmails e ON e.AuthorId = a.Id
            GROUP BY a.Id, a.Name, a.CreatedAt
            ORDER BY a.Name COLLATE NOCASE, a.Id;
            """;

        using var reader = command.ExecuteReader();
        var authors = new List<AuthorRecord>();
        while (reader.Read())
        {
            authors.Add(new AuthorRecord
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                CreatedAt = reader.GetString(2),
                EmailCount = reader.GetInt32(3)
            });
        }

        return authors;
    }

    public long CreateAuthor(string name)
    {
        var normalizedName = ValidateName(name);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Authors(Name, CreatedAt, UpdatedAt)
            VALUES($name, $now, $now)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$name", normalizedName);
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void RenameAuthor(long authorId, string name)
    {
        var normalizedName = ValidateName(name);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Authors
            SET Name = $name, UpdatedAt = $now
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$name", normalizedName);
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
        command.Parameters.AddWithValue("$id", authorId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("作者记录不存在或已被删除。");
    }

    public void DeleteAuthor(long authorId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Authors WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", authorId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("作者记录不存在或已被删除。");
    }

    public List<AuthorEmailRecord> ListEmails(long authorId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, AuthorId, Email, CreatedAt
            FROM AuthorEmails
            WHERE AuthorId = $authorId
            ORDER BY CreatedAt, Id;
            """;
        command.Parameters.AddWithValue("$authorId", authorId);

        using var reader = command.ExecuteReader();
        var emails = new List<AuthorEmailRecord>();
        while (reader.Read())
        {
            emails.Add(new AuthorEmailRecord
            {
                Id = reader.GetInt64(0),
                AuthorId = reader.GetInt64(1),
                Email = reader.GetString(2),
                CreatedAt = reader.GetString(3)
            });
        }

        return emails;
    }

    public long AddEmail(long authorId, string email)
    {
        var normalizedEmail = ValidateEmail(email);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        EnsureAuthorExists(connection, transaction, authorId);
        EnsureEmailIsUnique(connection, transaction, authorId, normalizedEmail);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AuthorEmails(AuthorId, Email, CreatedAt)
            VALUES($authorId, $email, $now)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$authorId", authorId);
        command.Parameters.AddWithValue("$email", normalizedEmail);
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
        var emailId = Convert.ToInt64(command.ExecuteScalar());
        transaction.Commit();
        return emailId;
    }

    public void UpdateEmail(long emailId, string email)
    {
        var normalizedEmail = ValidateEmail(email);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        long authorId;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT AuthorId FROM AuthorEmails WHERE Id = $id;";
            find.Parameters.AddWithValue("$id", emailId);
            var result = find.ExecuteScalar();
            if (result is null)
                throw new InvalidOperationException("邮箱记录不存在或已被删除。");
            authorId = Convert.ToInt64(result);
        }

        EnsureEmailIsUnique(connection, transaction, authorId, normalizedEmail, emailId);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE AuthorEmails SET Email = $email WHERE Id = $id;";
        command.Parameters.AddWithValue("$email", normalizedEmail);
        command.Parameters.AddWithValue("$id", emailId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("邮箱记录不存在或已被删除。");
        transaction.Commit();
    }

    public void DeleteEmail(long emailId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AuthorEmails WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", emailId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("邮箱记录不存在或已被删除。");
    }

    private static string ValidateName(string name)
    {
        var normalizedName = name?.Trim() ?? "";
        if (normalizedName.Length == 0)
            throw new InvalidOperationException("作者名称不能为空。");
        return normalizedName;
    }

    private static string ValidateEmail(string email)
    {
        var normalizedEmail = email?.Trim() ?? "";
        if (normalizedEmail.Length == 0)
            throw new InvalidOperationException("邮箱不能为空。");

        try
        {
            var parsed = new MailAddress(normalizedEmail);
            if (!string.Equals(parsed.Address, normalizedEmail, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("邮箱格式不正确。");
            return parsed.Address;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("邮箱格式不正确。", exception);
        }
    }

    private static void EnsureAuthorExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long authorId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM Authors WHERE Id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", authorId);
        if (command.ExecuteScalar() is null)
            throw new InvalidOperationException("作者记录不存在或已被删除。");
    }

    private static void EnsureEmailIsUnique(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long authorId,
        string email,
        long? excludedEmailId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = excludedEmailId is null
            ? """
                SELECT 1 FROM AuthorEmails
                WHERE AuthorId = $authorId AND Email = $email COLLATE NOCASE
                LIMIT 1;
                """
            : """
                SELECT 1 FROM AuthorEmails
                WHERE AuthorId = $authorId AND Email = $email COLLATE NOCASE AND Id <> $excludedId
                LIMIT 1;
                """;
        command.Parameters.AddWithValue("$authorId", authorId);
        command.Parameters.AddWithValue("$email", email);
        if (excludedEmailId is not null)
            command.Parameters.AddWithValue("$excludedId", excludedEmailId.Value);
        if (command.ExecuteScalar() is not null)
            throw new InvalidOperationException("该作者已存在相同邮箱。");
    }
}
