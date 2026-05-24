namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     What an attacker is going for when they probe a catalog path.
///     Tagged on every entry in <see cref="HoneypotPathDefinitions"/> so
///     consumers (dashboard chips, response policy, future analytics)
///     can read the intent without re-parsing the path.
/// </summary>
/// <remarks>
///     Categories are coarse-grained on purpose: each one corresponds to
///     a different exploit class. A single path can only belong to one
///     category -- when boundaries blur, the choice favours the more
///     specific class (e.g. <c>/wp-config.php.bak</c> is Config, not
///     Backup, because the value is the configuration secrets).
/// </remarks>
public enum HoneypotCategory
{
    /// <summary>Unknown / not categorised yet.</summary>
    None = 0,

    /// <summary>
    ///     Cloud + SSH + service credentials. The highest-value secrets:
    ///     <c>/.aws/credentials</c>, <c>/.ssh/id_rsa</c>,
    ///     <c>/.kube/config</c>, <c>/credentials.json</c>.
    /// </summary>
    Credentials = 1,

    /// <summary>
    ///     Application configuration that exposes secrets when leaked:
    ///     <c>/.env*</c>, <c>/web.config</c>, <c>/appsettings.json</c>,
    ///     <c>/wp-config.php.bak</c>.
    /// </summary>
    Config = 2,

    /// <summary>
    ///     Source-control directories or files leaked to the web:
    ///     <c>/.git/*</c>, <c>/.svn/*</c>, <c>/.hg/*</c>.
    /// </summary>
    VersionControl = 3,

    /// <summary>
    ///     Database administration UIs and bare dumps:
    ///     <c>/phpmyadmin*</c>, <c>/adminer.php</c>, <c>/backup.sql</c>,
    ///     <c>/db.sql</c>.
    /// </summary>
    Database = 4,

    /// <summary>
    ///     Known web shells, malicious by definition: <c>/c99.php</c>,
    ///     <c>/r57.php</c>, <c>/shell.php</c>, <c>/backdoor.php</c>.
    /// </summary>
    Webshell = 5,

    /// <summary>
    ///     Generic admin panels + dev-ops UIs intentionally exposed
    ///     (or accidentally): <c>/wp-admin</c>, <c>/cpanel</c>,
    ///     <c>/grafana</c>, <c>/jenkins</c>, <c>/portainer</c>.
    /// </summary>
    Admin = 6,

    /// <summary>
    ///     Debug / info-disclosure endpoints: <c>/phpinfo.php</c>,
    ///     <c>/elmah.axd</c>, <c>/_profiler</c>, <c>/server-status</c>.
    /// </summary>
    Debug = 7,

    /// <summary>
    ///     Whole-site backup archives + log dumps:
    ///     <c>/backup.zip</c>, <c>/site.tar.gz</c>, <c>/error.log</c>.
    /// </summary>
    Backup = 8,

    /// <summary>
    ///     Cloud metadata SSRF probes: <c>/latest/meta-data</c>,
    ///     <c>/computeMetadata/v1</c>, <c>/metadata/v1</c>.
    /// </summary>
    Metadata = 9,

    /// <summary>
    ///     Path-traversal targets for the host OS:
    ///     <c>/etc/passwd</c>, <c>/proc/self/environ</c>,
    ///     <c>/windows/win.ini</c>.
    /// </summary>
    PathTraversal = 10,

    /// <summary>
    ///     Build artefacts + IDE / editor files leaked to web root:
    ///     <c>/package.json</c>, <c>/composer.lock</c>,
    ///     <c>/Dockerfile</c>, <c>/.idea*</c>, <c>/.DS_Store</c>.
    /// </summary>
    BuildArtifact = 11,

    /// <summary>
    ///     API documentation that shouldn't be public on prod:
    ///     <c>/swagger.json</c>, <c>/v3/api-docs</c>.
    /// </summary>
    ApiDoc = 12,

    /// <summary>
    ///     CGI / legacy file-manager exploit surfaces:
    ///     <c>/cgi-bin*</c>, <c>/fckeditor*</c>, <c>/kcfinder*</c>.
    /// </summary>
    Cgi = 13,

    /// <summary>
    ///     CMS-specific (Drupal, Magento, Joomla) probe paths:
    ///     <c>/sites/default/*</c>, <c>/user/login</c>,
    ///     <c>/downloader</c>, <c>/app/etc/local.xml</c>.
    /// </summary>
    Cms = 14
}
