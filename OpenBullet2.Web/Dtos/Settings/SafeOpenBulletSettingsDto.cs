namespace OpenBullet2.Web.Dtos.Settings;

/// <summary>
/// Safe settings for the OpenBullet 2 application.
/// </summary>
public class SafeOpenBulletSettingsDto
{
    /// <summary>
    /// Safe general settings.
    /// </summary>
    public SafeOBGeneralSettingsDto GeneralSettings { get; set; } = new();
}

/// <summary>
/// Safe general settings of OpenBullet 2.
/// </summary>
public class SafeOBGeneralSettingsDto
{
    /// <summary>
    /// The refresh interval for periodically displaying all jobs' progress and information
    /// in the job manager page (in milliseconds).
    /// </summary>
    public int JobManagerUpdateInterval { get; set; } = 1000;
}
