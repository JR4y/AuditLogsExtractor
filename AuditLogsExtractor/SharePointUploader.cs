using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;

    public class SharePointUploader
    {
        private readonly string _siteUrl;
        private readonly string _uploadFolder;
        private readonly string _username;
        private readonly string _password;

        public SharePointUploader(string siteUrl, string libraryPath, string username, string password)
        {
            _siteUrl = siteUrl;
            _uploadFolder = libraryPath;
            _username = username;
            _password = password;
    }

        public void UploadFile(string localFilePath, string sharepointRelativePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
                {
                    Console.WriteLine($"❌ Archivo local no encontrado: {localFilePath}");
                    return;
                }

                //Console.WriteLine("🔐 Autenticando contra SharePoint Online...");
                CookieContainer authCookies = GetAuthenticationCookies(_siteUrl, _username, _password);

                if (authCookies == null)
                {
                    Console.WriteLine("❌ No se pudieron obtener cookies de autenticación.");
                    return;
                }

                // Crear estructura de carpetas en SharePoint
                string relativeFolder = Path.GetDirectoryName(sharepointRelativePath)?.Replace("\\", "/") ?? "";
                if (!string.IsNullOrEmpty(relativeFolder))
                {
                    string[] folders = relativeFolder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    string currentFolder = _uploadFolder.TrimEnd('/');

                    foreach (var folder in folders)
                    {
                        currentFolder += "/" + folder;
                        EnsureSharePointFolder(_siteUrl, currentFolder, authCookies);
                    }
                }

                // Subir archivo real
                string uploadUrl = $"{_siteUrl.TrimEnd('/')}{(_uploadFolder.StartsWith("/") ? "" : "/")}{_uploadFolder.TrimEnd('/')}/{sharepointRelativePath.Replace("\\", "/")}";
                //Console.WriteLine($"📤 Subiendo archivo real a: {uploadUrl}");

                byte[] fileBytes = File.ReadAllBytes(localFilePath);

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
                    if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK)
                    {
                        //Console.WriteLine("✅ Archivo subido exitosamente.");
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ Respuesta inesperada: {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error subiendo archivo: {ex.Message}");
            }
        }

        private void EnsureSharePointFolder(string siteUrl, string folderRelativeUrl, CookieContainer authCookies)
        {
            string folderUrl = $"{siteUrl.TrimEnd('/')}{(folderRelativeUrl.StartsWith("/") ? "" : "/")}{folderRelativeUrl.TrimEnd('/')}";
            try
            {
                HttpWebRequest mkcolRequest = (HttpWebRequest)WebRequest.Create(folderUrl);
                mkcolRequest.Method = "MKCOL";
                mkcolRequest.CookieContainer = authCookies;

                using (HttpWebResponse mkcolResponse = (HttpWebResponse)mkcolRequest.GetResponse())
                {
                    if (mkcolResponse.StatusCode == HttpStatusCode.Created || mkcolResponse.StatusCode == HttpStatusCode.MethodNotAllowed)
                    {
                        // 201 Created: carpeta creada; 405 MethodNotAllowed: ya existía
                        //Console.WriteLine($"📁 Carpeta '{folderRelativeUrl}' verificada/creada.");
                    }
                    else
                    {
                        //Console.WriteLine($"⚠️ Respuesta inesperada al crear carpeta: {mkcolResponse.StatusCode}");
                    }
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse resp && resp.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    //Console.WriteLine($"📁 Carpeta '{folderRelativeUrl}' ya existía.");
                }
                else
                {
                    Console.WriteLine($"❌ Error creando carpeta '{folderRelativeUrl}': {ex.Message}");
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
                        //Console.WriteLine("🔒 Token recibido:");
                        //Console.WriteLine(token.Substring(0, Math.Min(100, token.Length)) + "...");
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
            catch (Exception ex)
            {
                Console.WriteLine("❌ Error en autenticación SAML: " + ex.Message);
            }

            return null;
        }
    }

