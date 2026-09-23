using Windows.Security.Credentials;

namespace LectureAssistant.App.Services;

/// <summary>Keeps the instructor's Anthropic API key in Windows Credential Locker (encrypted per user), never in settings files.</summary>
public sealed class SecretStore
{
    private const string Resource = "LectureAssistant";
    private const string AnthropicKeyName = "AnthropicApiKey";

    private readonly PasswordVault _vault = new();

    public string? GetAnthropicApiKey()
    {
        try
        {
            var credential = _vault.Retrieve(Resource, AnthropicKeyName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490)) // Element not found
        {
            return null;
        }
    }

    public void SetAnthropicApiKey(string? key)
    {
        ClearAnthropicApiKey();
        if (!string.IsNullOrWhiteSpace(key))
            _vault.Add(new PasswordCredential(Resource, AnthropicKeyName, key.Trim()));
    }

    public void ClearAnthropicApiKey()
    {
        try
        {
            _vault.Remove(_vault.Retrieve(Resource, AnthropicKeyName));
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490)) { }
    }
}
