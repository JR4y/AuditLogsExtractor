using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml;

public class SharePointUploader
{
    private readonly string _siteUrl;
    private readonly string _uploadFolder;
    private readonly string _username;
    private readonly string _password;
    private HashSet<string> _carpetasVerificadas = new HashSet<string>();

    private readonly object _folderLock = new object();
    private readonly object _authLock = new object();
    private CookieContainer _authCookies = null;
    private DateTime _ultimaAutenticacion = DateTime.MinValue;

    private const int MaxRetries = 5;
    private const int InitialDelayMs = 1000; // 1 segundo
    private const int MaxDelayMs = 10000;    // 10 segundos como tope

    public SharePointUploader(string siteUrl, string libraryPath, string username, string password)
    {
        _siteUrl = siteUrl;
        _uploadFolder = libraryPath;
        _username = username;
        _password = password;
    }

    public void SetCarpetasVerificadas(HashSet<string> carpetas)
    {
        _carpetasVerificadas = carpetas ?? new HashSet<string>();
    }

    public HashSet<string> GetCarpetasVerificadas()
    {
        lock (_folderLock)
        {
            return new HashSet<string>(_carpetasVerificadas);
        }
    }

    private bool EntidadTieneArbolCompleto(string entidad, HashSet<string> carpetas)
    {
        return carpetas.Count(c => c.StartsWith(entidad + "|", StringComparison.OrdinalIgnoreCase)) >= 256;
    }

    public void UploadFile(string localFilePath, string sharepointRelativePath, string entidad = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            {
                Console.WriteLine($"❌ Archivo local no encontrado: {localFilePath}");
                return;
            }

            CookieContainer authCookies;
            lock (_authLock)
            {
                authCookies = GetOrRefreshCookies();
            }

            if (authCookies == null)
            {
                Console.WriteLine("❌ No se obtuvieron cookies de autenticación.");
                return;
            }

            // Crear estructura de carpetas en SharePoint
            string relativeFolder = Path.GetDirectoryName(sharepointRelativePath)?.Replace("\\", "/") ?? "";
            if (!string.IsNullOrEmpty(relativeFolder))
            {
                string[] folders = relativeFolder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                // 🧠 Extraer entidad desde la ruta
                //string entidad = folders.Length >= 2 ? folders[folders.Length - 2] : null;

                // 🧱 Validar si la entidad ya tiene su árbol completo
                if (!string.IsNullOrEmpty(entidad) && EntidadTieneArbolCompleto(entidad, _carpetasVerificadas))
                {
                    //Console.WriteLine($"📁 Estructura completa detectada para entidad '{entidad}', se omite creación de carpetas.");
                }
                else
                {
                    string currentFolder = _uploadFolder.TrimEnd('/');

                    foreach (var folder in folders)
                    {
                        currentFolder += "/" + folder;

                        string id = null;

                        // Parte relativa real, sin incluir _uploadFolder
                        string relativeToUpload = currentFolder.Replace(_uploadFolder.Trim('/'), "").Trim('/');

                        var segments = relativeToUpload.Split('/');
                        if (segments.Length >= 2)
                        {
                            string entidad2 = segments[segments.Length - 2];
                            string prefijo = segments[segments.Length - 1];
                            id = $"{entidad2}|{prefijo}";
                        }

                        EnsureSharePointFolder(_siteUrl, currentFolder, authCookies, id);
                    }
                }
            }

            // Subir archivo real
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

                    break; // Éxito, salimos del while
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse resp && (int)resp.StatusCode == 429)
                {
                    retryCount++;
                    Console.WriteLine($"⚠️  SharePoint devolvió 429 (Too Many Requests). Reintentando ({retryCount}/{MaxRetries}) en {delayMs}ms...");

                    if (retryCount > MaxRetries)
                    {
                        Console.WriteLine($"❌ Fallo tras {MaxRetries} reintentos por 429.");
                        throw new Exception("Límite de reintentos alcanzado por error 429.", ex);
                    }

                    Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, MaxDelayMs); // backoff exponencial
                }
                catch (WebException ex) when (ex.Response is HttpWebResponse resp)
                {
                    Logger.Error($"Fallo en subida de archivo. HTTP {resp.StatusCode} - {resp.StatusDescription}");

                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Console.WriteLine("🔐 Posible cookie expirada o inválida");
                    }

                    throw new Exception($"❌ Fallo en subida. Código HTTP: {resp.StatusCode}, Descripción: {resp.StatusDescription}", ex);
                }
            }

            /*HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uploadUrl);
            request.Method = "PUT";
            request.ContentLength = fileBytes.Length;
            request.CookieContainer = authCookies;

            request.Headers.Add("Overwrite", "T");
            request.Headers.Add("Translate", "f");

            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(fileBytes, 0, fileBytes.Length);
            }

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new Exception($"Respuesta inesperada del servidor al subir archivo: {response.StatusCode}");
                    }
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse resp)
                {
                    Console.WriteLine(); // fuerza salto desde el progreso                  
                    Logger.Error($"Fallo en subida de archivo. HTTP {resp.StatusCode} - {resp.StatusDescription}");

                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Console.WriteLine("🔐 Posible cookie expirada o inválida");
                    }

                    throw new Exception($"❌ Fallo en subida. Código HTTP: {resp.StatusCode}, Descripción: {resp.StatusDescription}", ex);
                }
            }*/
        }
        catch (Exception ex)
        {
            //Console.WriteLine($"❌ Error subiendo archivo: {ex.Message}");
            throw; // <== esta línea es la que falta
        }
    }

    private void EnsureSharePointFolder(string siteUrl, string folderRelativeUrl, CookieContainer authCookies, string carpetaId = null)
    {
        if (!string.IsNullOrEmpty(carpetaId))
        {
            lock (_folderLock)
            {
                if (_carpetasVerificadas.Contains(carpetaId))
                    return;

                _carpetasVerificadas.Add(carpetaId);
                //Logger.Trace($"📁 Carpeta verificada agregada: {carpetaId}");
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
                // Ya existe la carpeta
            }
            else
            {
                throw new Exception($"Error creando carpeta '{folderRelativeUrl}': {ex.Message}", ex);
            }
        }
    }

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
                var statusCode = (int)resp.StatusCode;
                var statusDesc = resp.StatusDescription;
                throw new Exception($"❌ Error en autenticación SAML: HTTP {statusCode} - {statusDesc}", ex);
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
            if (_authCookies != null && (DateTime.Now - _ultimaAutenticacion).TotalMinutes < 15)
                return _authCookies;

            _authCookies = GetAuthenticationCookies(_siteUrl, _username, _password);
            _ultimaAutenticacion = DateTime.Now;
            return _authCookies;
        }
    }

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
                Console.WriteLine("❌ No se obtuvieron cookies de autenticación.");
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

            //Console.WriteLine($"📥 Archivo descargado exitosamente: {localDestinationPath}");
        }
        catch (WebException ex)
        {
            if (ex.Response is HttpWebResponse resp && resp.StatusCode == HttpStatusCode.NotFound)
            {
                //Console.WriteLine($"⚠️ Archivo no encontrado en SharePoint: {sharepointRelativePath}");
            }
            else
            {
                //Console.WriteLine($"❌ Error al descargar archivo '{sharepointRelativePath}': {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error inesperado en descarga de archivo '{sharepointRelativePath}': {ex.Message}");
        }
    }
}

