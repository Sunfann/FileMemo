using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FileMemo.App.Data;
using FileMemo.App.Models;

namespace FileMemo.App.Services;

/// <summary>
/// 云同步（需求 3.12，P1 预留实现）。
///
/// 设计约定（与需求「按场景区分权威」一致）：
///   - 云同步**只同步 SQLite 主库**（不上传附件目录之外的散落文件）；
///   - 本地固定盘以 SQLite 为准；移动盘 / NAS 以 sidecar 为准；
///   - 冲突时保留两份 + 生成 .conflict 副本，交由用户手动选择；
///   - 传输可开启端到端加密（复用 CryptoService 的 DPAPI 密钥流）。
///
/// 默认关闭；用户显式开启并配置 WebDAV / OneDrive 后才会执行。
/// </summary>
public sealed class CloudSyncService
{
    private readonly Database _db;
    private readonly SettingsService _settings;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public CloudSyncService(Database db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public bool Enabled => _settings.CloudSyncEnabled && _settings.CloudProvider != CloudProvider.None;

    /// <summary>上传本地主库到云端（WebDAV：PUT 到 endpoint/SuperNote/supernote.db）。</summary>
    public async Task<(bool ok, string message)> UploadAsync()
    {
        if (!Enabled) return (false, "云同步未开启");
        try
        {
            if (!File.Exists(_db.DbPath)) return (false, "本地主库不存在");

            byte[] data = await File.ReadAllBytesAsync(_db.DbPath);
            if (_settings.CloudSyncEncrypt)
                data = CryptoService.ProtectBytes(data);

            var uri = BuildRemoteUri("supernote.db", ".enc");
            using var req = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new ByteArrayContent(data)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            ApplyAuth(req);

            var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode
                ? (true, "已上传主库：" + uri)
                : (false, $"上传失败 {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (false, "上传异常：" + ex.Message); }
    }

    /// <summary>从云端下载主库（本地冲突时先备份为 .conflict）。</summary>
    public async Task<(bool ok, string message)> DownloadAsync()
    {
        if (!Enabled) return (false, "云同步未开启");
        try
        {
            var uri = BuildRemoteUri("supernote.db", ".enc");
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyAuth(req);

            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (false, $"下载失败 {(int)resp.StatusCode}");

            byte[] remote = await resp.Content.ReadAsByteArrayAsync();
            if (_settings.CloudSyncEncrypt)
                remote = CryptoService.UnprotectBytes(remote);

            // 冲突处理：保留本地副本，再覆盖
            if (File.Exists(_db.DbPath))
            {
                var backup = _db.DbPath + ".conflict-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(_db.DbPath, backup, overwrite: true);
            }
            await File.WriteAllBytesAsync(_db.DbPath, remote);
            return (true, "已下载主库（原库已备份为 .conflict）");
        }
        catch (Exception ex) { return (false, "下载异常：" + ex.Message); }
    }

    public async Task<(bool ok, string message)> TestConnectionAsync()
    {
        if (!Enabled) return (false, "云同步未开启");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, BuildRemoteUri("", ""));
            ApplyAuth(req);
            var resp = await Http.SendAsync(req);
            return (true, $"连接可用（{(int)resp.StatusCode}）");
        }
        catch (Exception ex) { return (false, "无法连接：" + ex.Message); }
    }

    private Uri BuildRemoteUri(string fileName, string suffix)
    {
        var baseUri = _settings.CloudSyncEndpoint.TrimEnd('/');
        var path = "SuperNote/" + fileName + suffix;
        return new Uri($"{baseUri}/{path}");
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        if (_settings.CloudProvider == CloudProvider.WebDav)
        {
            var user = _settings.CloudSyncUser;
            var pwd = string.IsNullOrEmpty(_settings.CloudSyncPassword)
                ? CryptoService.UnprotectString(_settings.CloudSyncPasswordEncrypted)
                : _settings.CloudSyncPassword;
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pwd}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }
        // OneDrive：预留 OAuth Bearer 注入点
        else if (_settings.CloudProvider == CloudProvider.OneDrive && !string.IsNullOrEmpty(_settings.CloudSyncPassword))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.CloudSyncPassword);
        }
    }
}
