// Refactorización intermedia: limpieza, regiones y renombrado de miembros (sin modificar lógica interna)
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml;

namespace AuditLogsExtractor
{
    public class SharePointUploader
    {
        #region Fields and Constructor

        private readonly string _siteUrl;
        private readonly string _uploadFolder;
        private readonly string _username;
        private readonly string _password;
        private HashSet<string> _verifiedFolders = new HashSet<string>();

        private readonly object _folderLock = new object();
        private readonly object _authLock = new object();
        private CookieContainer _authCookies = null;
        private DateTime _lastAuthTime = DateTime.MinValue;

        private const int MaxRetries = 5;
        private const int InitialDelayMs = 1000;
        private const int MaxDelayMs = 10000;

        public SharePointUploader(string siteUrl, string libraryPath, string username, string password)
        {
            _siteUrl = siteUrl;
            _uploadFolder = libraryPath;
            _username = username;
            _password = password;
        }

        #endregion

        #region Folder Tracking

        public void SetVerifiedFolders(HashSet<string> folders)
        {
            _verifiedFolders = folders ?? new HashSet<string>();
        }

        public HashSet<string> GetVerifiedFolders()
        {
            lock (_folderLock)
            {
                return new HashSet<string>(_verifiedFolders);
            }
        }

        private bool HasCompleteFolderTree(string entity, HashSet<string> folders)
        {
            return folders.Count(c => c.StartsWith(entity + "|", StringComparison.OrdinalIgnoreCase)) >= 256;
        }

        #endregion

        #region Upload Methods

        public void UploadFile(string localFilePath, string sharepointRelativePath, string entity = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
                {
                    Logger.Log($"❌ Archivo local no encontrado: {localFilePath}", "ERROR");
                    return;
                }

                CookieContainer authCookies;
                lock (_authLock)
                {
                    authCookies = GetOrRefreshCookies();
                }

                if (authCookies == null)
                {
                    Logger.Log("❌ No se obtuvieron cookies de autenticación.", "ERROR");
                    return;
                }

                string relativeFolder = Path.GetDirectoryName(sharepointRelativePath)?.Replace("\\", "/") ?? "";
                if (!string.IsNullOrEmpty(relativeFolder))
                {
                    string[] folders = relativeFolder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                    if (!string.IsNullOrEmpty(entity) && HasCompleteFolderTree(entity, _verifiedFolders))
                    {
                        // skip
                    }
                    else
                    {
                        string currentFolder = _uploadFolder.TrimEnd('/');

                        foreach (var folder in folders)
                        {
                            currentFolder += "/" + folder;

                            string id = null;
                            string relativeToUpload = currentFolder.Replace(_uploadFolder.Trim('/'), "").Trim('/');

                            var segments = relativeToUpload.Split('/');
                            if (segments.Length >= 2)
                            {
                                string entityPart = segments[segments.Length - 2];
                                string prefix = segments[segments.Length - 1];
                                id = $"{entityPart}|{prefix}";
                            }

                            EnsureSharePointFolder(_siteUrl, currentFolder, authCookies, id);
                        }
                    }
                }

                string uploadUrl = $"{_siteUrl.TrimEnd('/')}{(_uploadFolder.StartsWith("/") ? "" : "/")}{_uploadFolder.TrimEnd('/')}/{sharepointRelativePath.Replace("\\", "/")}";
                byte[] fileBytes = File.ReadAllBytes(localFilePath);

                int retryCount = 0;
                int delayMs = InitialDelayMs;

                while (true)
                {
                    try
                    {
                        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uploadUrl);
                        request.Method = "PUT";
                        request.ContentLength = fileBytes.Length;
                        request.CookieContainer = authCookies;
                        request.Headers.Add("Overwrite", "T");
                        request.Headers.Add("Translate", "f");

                        using (Stream requestStream = request.GetRequestStream())
                        {
                            requestStream.Write(fileBytes, 0, fileBytes.Length);
                        }

                        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                        {
                            if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK)
                            {
                                throw new Exception($"Respuesta inesperada del servidor al subir archivo: {response.StatusCode}");
                            }
                        }

                        break;
                    }
                    catch (WebException ex) when (ex.Response is HttpWebResponse resp && (int)resp.StatusCode == 429)
                    {
                        retryCount++;
                        Logger.Log($"SharePoint devolvió 429 (Too Many Requests). Reintentando ({retryCount}/{MaxRetries}) en {delayMs}ms...", "ERROR");

                        if (retryCount > MaxRetries)
                        {
                            Logger.Log($"❌ Fallo tras {MaxRetries} reintentos por 429.", "ERROR");
                            throw new Exception("Límite de reintentos alcanzado por error 429.", ex);
                        }

                        Thread.Sleep(delayMs);
                        delayMs = Math.Min(delayMs * 2, MaxDelayMs);
                    }
                    catch (WebException ex) when (ex.Response is HttpWebResponse resp)
                    {
                        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                        {
                            Logger.Log("🔐 Posible cookie expirada o inválida", "ERROR");
                        }

                        throw new Exception($"❌ Fallo en subida. Código HTTP: {resp.StatusCode}, Descripción: {resp.StatusDescription}", ex);
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        public void UploadZipFile(string zipPath, string sharepointRelativePath, string entity = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                {
                    Logger.Log($"❌ Archivo ZIP no encontrado: {zipPath}", "ERROR");
                    return;
                }

                CookieContainer authCookies;
                lock (_authLock)
                {
                    authCookies = GetOrRefreshCookies();
                }

                if (authCookies == null)
                {
                    Logger.Log("❌ No se obtuvieron cookies de autenticación.", "ERROR");
                    return;
                }

                string relativeFolder = Path.GetDirectoryName(sharepointRelativePath)?.Replace("\\", "/") ?? "";
                if (!string.IsNullOrEmpty(relativeFolder))
                {
                    string[] folders = relativeFolder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                    if (!string.IsNullOrEmpty(entity) && HasCompleteFolderTree(entity, _verifiedFolders))
                    {
                        // skip
                    }
                    else
                    {
                        string currentFolder = _uploadFolder.TrimEnd('/');
                        foreach (var folder in folders)
                        {
                            currentFolder += "/" + folder;

                            string id = null;
                            string relativeToUpload = currentFolder.Replace(_uploadFolder.Trim('/'), "").Trim('/');

                            var segments = relativeToUpload.Split('/');
                            if (segments.Length >= 2)
                            {
                                string entityPart = segments[segments.Length - 2];
                                string prefix = segments[segments.Length - 1];
                                id = $"{entityPart}|{prefix}";
                            }

                            EnsureSharePointFolder(_siteUrl, currentFolder, authCookies, id);
                        }
                    }
                }

                string uploadUrl = $"{_siteUrl.TrimEnd('/')}{(_uploadFolder.StartsWith("/") ? "" : "/")}{_uploadFolder.TrimEnd('/')}/{sharepointRelativePath.Replace("\\", "/")}";
                byte[] fileBytes = File.ReadAllBytes(zipPath);

                int retryCount = 0;
                int delayMs = InitialDelayMs;

                while (true)
                {
                    try
                    {
                        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uploadUrl);
                        request.Method = "PUT";
                        request.ContentLength = fileBytes.Length;
                        request.CookieContainer = authCookies;
                        request.Headers.Add("Overwrite", "T");
                        request.Headers.Add("Translate", "f");
                        request.ContentType = "application/zip";

                        using (Stream requestStream = request.GetRequestStream())
                        {
                            requestStream.Write(fileBytes, 0, fileBytes.Length);
                        }

                        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                        {
                            if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK)
                            {
                                throw new Exception($"Respuesta inesperada al subir ZIP: {response.StatusCode}");
                            }
                        }

                        break;
                    }
                    catch (WebException ex) when (ex.Response is HttpWebResponse resp && (int)resp.StatusCode == 429)
                    {
                        retryCount++;
                        Logger.Log($"⚠️  SharePoint 429 (Too Many Requests). Reintentando ({retryCount}/{MaxRetries}) en {delayMs}ms...", "ERROR");

                        if (retryCount > MaxRetries)
                        {
                            Logger.Log($"❌ Fallo tras {MaxRetries} reintentos por 429.", "ERROR");
                            throw new Exception("Límite de reintentos alcanzado por error 429.", ex);
                        }

                        Thread.Sleep(delayMs);
                        delayMs = Math.Min(delayMs * 2, MaxDelayMs);
                    }
                    catch (WebException ex) when (ex.Response is HttpWebResponse resp)
                    {
                        Logger.Log($"Fallo en subida de ZIP. HTTP {resp.StatusCode} - {resp.StatusDescription}", "ERROR");

                        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                        {
                            Logger.Log("🔐 Posible cookie expirada o inválida", "ERROR");
                        }

                        throw new Exception($"❌ Fallo en subida ZIP. Código HTTP: {resp.StatusCode}, Descripción: {resp.StatusDescription}", ex);
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        #endregion

        #region Folder Creation

        private void EnsureSharePointFolder(string siteUrl, string folderRelativeUrl, CookieContainer authCookies, string folderId = null)
        {
            if (!string.IsNullOrEmpty(folderId))
            {
                lock (_folderLock)
                {
                    if (_verifiedFolders.Contains(folderId))
                        return;

                    _verifiedFolders.Add(folderId);
                }
            }

            string folderUrl = $"{siteUrl.TrimEnd('/')}{(folderRelativeUrl.StartsWith("/") ? "" : "/")}{folderRelativeUrl.TrimEnd('/')}";

            try
            {
                HttpWebRequest mkcolRequest = (HttpWebRequest)WebRequest.Create(folderUrl);
                mkcolRequest.Method = "MKCOL";
                mkcolRequest.CookieContainer = authCookies;

                using (HttpWebResponse mkcolResponse = (HttpWebResponse)mkcolRequest.GetResponse())
                {
                    if (mkcolResponse.StatusCode != HttpStatusCode.Created &&
                        mkcolResponse.StatusCode != HttpStatusCode.MethodNotAllowed)
                    {
                        throw new Exception($"Respuesta inesperada al crear carpeta: {mkcolResponse.StatusCode}");
                    }
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse resp && resp.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    // La carpeta ya existe
                }
                else
                {
                    throw new Exception($"Error creando carpeta '{folderRelativeUrl}': {ex.Message}", ex);
                }
            }
        }

        #endregion

        #region Authentication

        private CookieContainer GetAuthenticationCookies(string siteUrl, string username, string password)
        {
            try
            {
                string stsUrl = "https://login.microsoftonline.com/extSTS.srf";
                string soapEnvelope = $"<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:a=\"http://www.w3.org/2005/08/addressing\" xmlns:u=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" +
                    "<s:Header>" +
                    "<a:Action s:mustUnderstand=\"1\">http://schemas.xmlsoap.org/ws/2005/02/trust/RST/Issue</a:Action>" +
                    "<a:To s:mustUnderstand=\"1\">https://login.microsoftonline.com/extSTS.srf</a:To>" +
                    "<o:Security s:mustUnderstand=\"1\" xmlns:o=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">" +
                    $"<o:UsernameToken><o:Username>{username}</o:Username><o:Password>{password}</o:Password></o:UsernameToken>" +
                    "</o:Security></s:Header><s:Body>" +
                    "<t:RequestSecurityToken xmlns:t=\"http://schemas.xmlsoap.org/ws/2005/02/trust\">" +
                    "<wsp:AppliesTo xmlns:wsp=\"http://schemas.xmlsoap.org/ws/2004/09/policy\">" +
                    $"<a:EndpointReference><a:Address>{siteUrl}</a:Address></a:EndpointReference>" +
                    "</wsp:AppliesTo><t:RequestType>http://schemas.xmlsoap.org/ws/2005/02/trust/Issue</t:RequestType>" +
                    "</t:RequestSecurityToken></s:Body></s:Envelope>";

                byte[] bodyBytes = Encoding.UTF8.GetBytes(soapEnvelope);

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(stsUrl);
                request.Method = "POST";
                request.ContentType = "application/soap+xml; charset=utf-8";
                request.ContentLength = bodyBytes.Length;

                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(bodyBytes, 0, bodyBytes.Length);
                }

                string token = null;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseXml = reader.ReadToEnd();
                    var doc = new XmlDocument();
                    doc.LoadXml(responseXml);

                    XmlNode binaryTokenNode = doc.GetElementsByTagName("wsse:BinarySecurityToken")[0];
                    if (binaryTokenNode != null)
                    {
                        token = binaryTokenNode.InnerText;
                    }
                }

                if (string.IsNullOrEmpty(token)) return null;

                string signInUrl = new Uri(new Uri(siteUrl), "/_forms/default.aspx?wa=wsignin1.0").ToString();
                HttpWebRequest signInRequest = (HttpWebRequest)WebRequest.Create(signInUrl);
                signInRequest.Method = "POST";
                signInRequest.ContentType = "application/x-www-form-urlencoded";
                signInRequest.UserAgent = "Mozilla/5.0";
                signInRequest.Accept = "text/html,application/xhtml+xml,application/xml";
                signInRequest.Headers.Add("Accept-Encoding", "gzip, deflate");
                signInRequest.Headers.Add("Accept-Language", "es-ES");
                signInRequest.KeepAlive = false;

                byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
                signInRequest.ContentLength = tokenBytes.Length;

                CookieContainer cookieContainer = new CookieContainer();
                signInRequest.CookieContainer = cookieContainer;

                using (Stream requestStream = signInRequest.GetRequestStream())
                {
                    requestStream.Write(tokenBytes, 0, tokenBytes.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)signInRequest.GetResponse())
                {
                    return signInRequest.CookieContainer;
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse resp)
                {
                    throw new Exception($"❌ Error en autenticación SAML: HTTP {(int)resp.StatusCode} - {resp.StatusDescription}", ex);
                }
                else
                {
                    throw new Exception("❌ Error desconocido en autenticación SAML (sin respuesta HTTP).", ex);
                }
            }
            catch (Exception ex)
            {
                throw new Exception("❌ Error inesperado en autenticación SAML.", ex);
            }
        }

        private CookieContainer GetOrRefreshCookies()
        {
            lock (_authLock)
            {
                if (_authCookies != null && (DateTime.Now - _lastAuthTime).TotalMinutes < 15)
                    return _authCookies;

                _authCookies = GetAuthenticationCookies(_siteUrl, _username, _password);
                _lastAuthTime = DateTime.Now;
                return _authCookies;
            }
        }

        #endregion

        #region File Download

        public void DownloadFile(string sharepointRelativePath, string localDestinationPath)
        {
            try
            {
                CookieContainer authCookies;
                lock (_authLock)
                {
                    authCookies = GetOrRefreshCookies();
                }

                if (authCookies == null)
                {
                    Logger.Log("❌ No se obtuvieron cookies de autenticación.", "ERROR");
                    return;
                }

                string fileUrl = $"{_siteUrl.TrimEnd('/')}{(_uploadFolder.StartsWith("/") ? "" : "/")}{_uploadFolder.TrimEnd('/')}/{sharepointRelativePath.Replace("\\", "/")}";

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(fileUrl);
                request.Method = "GET";
                request.CookieContainer = authCookies;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (FileStream fileStream = new FileStream(localDestinationPath, FileMode.Create, FileAccess.Write))
                {
                    responseStream.CopyTo(fileStream);
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse resp && resp.StatusCode == HttpStatusCode.NotFound)
                {
                    // Archivo no encontrado en SharePoint
                }
                else
                {
                    Logger.Log($"❌ Error al descargar archivo '{sharepointRelativePath}': {ex.Message}", "ERROR");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error inesperado en descarga de archivo '{sharepointRelativePath}': {ex.Message}", "ERROR");
            }
        }

        #endregion
    }
}
