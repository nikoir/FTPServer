using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FTPServer
{
    enum DataConnectionType
    {
        Active, Passive
    }
    class ClientConnection : IDisposable
    {
        private TcpClient _controlClient;
        private TcpClient _dataClient; //объект, используемый для передачи данных с помощью пассивного режима
        private TcpListener _passiveListener;
        private IPEndPoint _dataEndpoint;

        private NetworkStream _controlStream;
        private StreamReader _controlReader;
        private StreamWriter _controlWriter;

        private DataConnectionType _dataConnectionType = DataConnectionType.Active;

        private string _username;
        private string _transferType; //тип передачи данных (реализован I - двоичный тип передачи)
        private string _currentDirectory = "C:\\ftp_server"; //текущая папка с которой работает клиент
        private string _root = null; //корневой элемент
        private string _dumpFolder = null; //папка для сохранения удаленных файлов
        private StreamReader _dataReader;
        private StreamWriter _dataWriter;
        private X509Certificate2 _cert = null;
        private SslStream _sslStream;
        private bool _disposed = false;
        private List<string> _validCommands = new List<string>();
        private UserStore _currentUser;

        private string CheckUser()
        {
            if (_currentUser == null)
            {
                return "530 Not logged in";
            }

            return null;
        }

        public ClientConnection(TcpClient client)
        {
            _controlClient = client;

            _controlStream = _controlClient.GetStream();

            _controlReader = new StreamReader(_controlStream, Encoding.GetEncoding(1251)); //Кодировка 1251 - для чтения русских символов
            //_controlReader = new StreamReader(_controlStream, Encoding.UTF8);
            _controlWriter = new StreamWriter(_controlStream);
        }

        public void HandleClient(object obj)
        {
            _controlWriter.WriteLine("220 Service Ready.");
            _controlWriter.Flush();

            string renameFrom = null;
            string line = null;

            _validCommands.AddRange(new string[] { "AUTH", "USER", "PASS", "QUIT", "HELP", "NOOP" });

            try
            {
                while (!string.IsNullOrEmpty(line = _controlReader.ReadLine()))
                {
                    string response = null;

                    string[] command = line.Split(' ');

                    string cmd = command[0].ToUpperInvariant();
                    string arguments = command.Length > 1 ? line.Substring(command[0].Length + 1) : null;

                    if (string.IsNullOrWhiteSpace(arguments))
                        arguments = null;

                    if (!_validCommands.Contains(cmd))
                    {
                        response = CheckUser();
                    }

                    if (cmd != "RNTO")
                    {
                        renameFrom = null;
                    }
                    if (response == null)
                    {
                        switch (cmd)
                        {
                            case "USER":
                                response = User(arguments);
                                break;
                            case "PASS":
                                response = Password(arguments);
                                break;
                            case "CWD":
                                response = ChangeWorkingDirectory(arguments); //+
                                break;
                            case "CDUP":
                                response = ChangeWorkingDirectory(".."); //+
                                break;
                            case "PWD":
                                response = PrintWorkingDirectory();
                                break;
                            case "QUIT":
                                response = "221 Service closing control connection";
                                break;
                            case "TYPE":
                                string[] splitArgs = arguments.Split(' ');
                                response = Type(splitArgs[0], splitArgs.Length > 1 ? splitArgs[1] : null);
                                break;
                            case "PASV": //Пассивный режим подключения
                                response = Passive();
                                break;
                            case "PORT":
                                response = Port(arguments); //Задаем порт для активного соединения
                                break;
                            case "LIST":
                                response = List(arguments); //+
                                break;
                            case "RETR":
                                response = Retrieve(arguments);
                                break;
                            case "STOR":
                                response = Store(arguments);
                                break;
                            case "DELE":
                                response = Delete(arguments);
                                break;
                            //case "RMD":
                            //    response = TransferDir(NormalizeFilename(arguments), Path.Combine(_dumpFolder, "Folder" + NormalizeDateTime()));
                            //    break;
                            case "MKD":
                                response = CreateDir(arguments);
                                break;
                            case "RNFR":
                                renameFrom = arguments;
                                response = "350 Requested file action pending further information";
                                break;
                            case "RNTO":
                                response = Rename(renameFrom, arguments);
                                break;
                            // Extensions defined by rfc 2228
                            //case "AUTH":
                            //    response = Auth(arguments);
                            //    break;
                            default:
                                response = "502 Command not implemented";
                                break;
                        }
                    }

                    if (_controlClient == null || !_controlClient.Connected)
                    {
                        break;
                    }
                    else
                    {
                        _controlWriter.WriteLine(response);
                        _controlWriter.Flush();

                        if (response.StartsWith("221"))
                        {
                            break;
                        }
                        //if (cmd == "AUTH")
                        //{
                        //    _cert = new X509Certificate2("D:\\Program Files (x86)\\Microsoft Visual Studio 12.0\\VC\\bin\\x86_amd64\\server.pfx", "12345");

                        //    _sslStream = new SslStream(_controlStream, false);

                        //    _sslStream.AuthenticateAsServer(_cert, false, System.Security.Authentication.SslProtocols.Default, false);

                        //    _controlReader = new StreamReader(_sslStream);
                        //    _controlWriter = new StreamWriter(_sslStream);
                        //}
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                //throw;
            }
            Dispose();
        }

        #region FTP Commands
        
        private string Retrieve(string pathname)
        {
            pathname = NormalizeFilename(pathname);
            if (IsPathValid(pathname))
            {
                if (File.Exists(pathname))
                {
                    if (_dataConnectionType == DataConnectionType.Active)
                    {
                        _dataClient = new TcpClient();
                        _dataClient.BeginConnect(_dataEndpoint.Address, _dataEndpoint.Port, DoRetrieve, pathname);
                    }
                    else
                    {
                        _passiveListener.BeginAcceptTcpClient(DoRetrieve, pathname);
                    }

                    return string.Format("150 Opening {0} mode data transfer for RETR", _dataConnectionType);
                }
            }
            return "550 File Not Found";
        }
        private static long CopyStream(Stream input, Stream output, int bufferSize) //input - поток для считывания файла на сервере, output - поток для передачи файла клиенту
        {
            byte[] buffer = new byte[bufferSize];
            int count = 0;
            long total = 0;
            //long _maxFileSize = 314572800;
            long _maxFileSize = 30720;

            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, count);
                total += count;
                if (total >= _maxFileSize)
                {
                    throw new Exception("550 File size too large.");
                }
            }

            return total;
        }
        private void DoRetrieve(IAsyncResult result)
        {
            if (_dataConnectionType == DataConnectionType.Active)
            {
                _dataClient.EndConnect(result);
            }
            else
            {
                _dataClient = _passiveListener.EndAcceptTcpClient(result);
            }

            string pathname = (string)result.AsyncState;

            try
            {
                using (NetworkStream dataStream = _dataClient.GetStream())
                {
                    using (FileStream fs = new FileStream(pathname, FileMode.Open, FileAccess.Read))
                    {
                        CopyStream(fs, dataStream, 4096);
                        _dataClient.Close();
                        _dataClient = null;
                        _controlWriter.WriteLine("226 Closing data connection, file transfer successful");
                        _controlWriter.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                _dataClient.Close();
                _dataClient = null;
                _controlWriter.WriteLine(ex.Message);
                _controlWriter.Flush();
            }
        }


        private string User(string username)
        {
            _username = username;

            return "331 Username ok, need password";
        }

        private string Password(string password)
        {
            string userGroup = UserStore.GetUserGroup(_username, password);
            if (userGroup != null)
            {
                _currentUser = new UserStore("C:\\ftp_server", _username, userGroup);
                _root = _currentUser.UserRoot;
                _currentDirectory = _root;
                return "230 User logged in";
            }
            else
            {
                return "530 Not logged in";
            }
        }

        private string ChangeWorkingDirectory(string pathname)
        {
            if (pathname == "/")
            {
                _currentDirectory = _root;
            }
            else
            {
                string newDir;

                if (pathname.StartsWith("/"))
                {
                    pathname = pathname.Substring(1).Replace('/', '\\');
                    //newDir = Path.Combine(_root, pathname);
                    newDir = _root + "\\" + pathname;
                }
                else
                {
                    pathname = pathname.Replace('/', '\\');
                    //newDir = Path.Combine(_currentDirectory, pathname);
                    newDir = _currentDirectory + "\\" + pathname;
                }

                if (Directory.Exists(newDir))
                {
                    _currentDirectory = new DirectoryInfo(newDir).FullName;

                    if (!IsPathValid(_currentDirectory))
                    {
                        _currentDirectory = _root;
                        return "550 Access denied or file/folder not found!";
                    }
                    
                }
                else
                {
                    _currentDirectory = _root;
                    return "550 Access denied or file/folder not found!";
                }
            }
            return "250 Changed to new directory";
        }

        private string PrintWorkingDirectory()
        {
            string current = _currentDirectory.Replace(_root, string.Empty).Replace('\\', '/');

            if (current.Length == 0)
            {
                current = "/";
            }
            return string.Format("257 \"{0}\" is current directory.", current); ;
        }

        private string Type(string typeCode, string formatControl)
        {
            string response = "500 ERROR";

            switch (typeCode)
            {
                case "A":
                case "I":
                    _transferType = typeCode;
                    response = "200 OK";
                    break;
                case "E":
                case "L":
                default:
                    response = "504 Command not implemented for that parameter.";
                    break;
            }

            if (formatControl != null)
            {
                switch (formatControl)
                {
                    case "N":
                        response = "200 OK";
                        break;
                    case "T":
                    case "C":
                    default:
                        response = "504 Command not implemented for that parameter.";
                        break;
                }
            }

            return response;
        }

        private string Port(string hostPort)
        {
            _dataConnectionType = DataConnectionType.Active;

            string[] ipAndPort = hostPort.Split(',');

            byte[] ipAddress = new byte[4];
            byte[] port = new byte[2];

            for (int i = 0; i < 4; i++)
            {
                ipAddress[i] = Convert.ToByte(ipAndPort[i]);
            }

            for (int i = 4; i < 6; i++)
            {
                port[i - 4] = Convert.ToByte(ipAndPort[i]);
            }

            if (BitConverter.IsLittleEndian)
                Array.Reverse(port);

            _dataEndpoint = new IPEndPoint(new IPAddress(ipAddress), BitConverter.ToInt16(port, 0));

            return "200 Data Connection Established";
        }

        private string Passive()
        {
            _dataConnectionType = DataConnectionType.Passive; //Устанавливаем пассивный тип соединения

            IPAddress localIp = ((IPEndPoint)_controlClient.Client.LocalEndPoint).Address; //Получаем текущий IP-адрес сервера

            _passiveListener = new TcpListener(localIp, 0); //0 - сервер сам выделяет доступный порт.
            _passiveListener.Start(); //Начинаем прослушивать выделенный порт

            IPEndPoint passiveListenerEndpoint = (IPEndPoint)_passiveListener.LocalEndpoint;

            byte[] address = passiveListenerEndpoint.Address.GetAddressBytes();
            short port = (short)passiveListenerEndpoint.Port;

            byte[] portArray = BitConverter.GetBytes(port);

            if (BitConverter.IsLittleEndian) //Если используется порядок записи LittleEndian, то перезаписываем наоборот
                Array.Reverse(portArray);
            return string.Format("227 Entering Passive Mode ({0},{1},{2},{3},{4},{5})", address[0], address[1], address[2], address[3], portArray[0], portArray[1]);
        }

        private string List(string pathname)
        {
            pathname = NormalizeFilename(pathname);
            if (IsPathValid(pathname))
            {
                if (_dataConnectionType == DataConnectionType.Active)
                {
                    _dataClient = new TcpClient();
                    _dataClient.BeginConnect(_dataEndpoint.Address, _dataEndpoint.Port, DoList, pathname); //Подключаемя к клиенту по активному режиму по IP-адресу и порту, который указал клиент с помощью команды PORT
                }
                else
                {
                    _passiveListener.BeginAcceptTcpClient(DoList, pathname); //Тут работаем в пассивном режиме, ждем когда клиент подключится
                }

                return string.Format("150 Opening {0} mode data transfer for LIST", _dataConnectionType);
            }
            return "450 Requested file action not taken";
        }
        private string Store(string pathname)
        {
            pathname = NormalizeFilename(pathname);

            if (pathname != null)
            {
                if (_dataConnectionType == DataConnectionType.Active)
                {
                    _dataClient = new TcpClient();
                    _dataClient.BeginConnect(_dataEndpoint.Address, _dataEndpoint.Port, DoDataConnectionOperation, pathname);
                }
                else
                {
                    _passiveListener.BeginAcceptTcpClient(DoDataConnectionOperation, pathname);
                }

                return string.Format("150 Opening {0} mode data transfer for STOR", _dataConnectionType);
            }

            return "450 Requested file action not taken";
        }
        private string Delete(string pathname)
        {
            pathname = NormalizeFilename(pathname);

            if (pathname != null)
            {
                if (File.Exists(pathname))
                {
                    string FileName = Path.GetFileNameWithoutExtension(pathname);
                    string Extension = Path.GetExtension(pathname);
                    FileName += NormalizeDateTime();
                    File.Copy(pathname, _dumpFolder + "\\" + FileName + Extension, true);
                    File.Delete(pathname);
                }
                else
                {
                    return "550 File Not Found";
                }

                return "250 Requested file action okay, completed";
            }

            return "550 File Not Found";
        }

        //private string TransferDir(string pathname, string destPath)
        //{
        //    if (pathname != null)
        //    {
        //        DirectoryInfo dir = new DirectoryInfo(pathname);
        //        if (Directory.Exists(pathname))
        //        {
        //            DirectoryInfo sourseName = new DirectoryInfo(destPath);
        //            if (!sourseName.Exists)
        //                Directory.CreateDirectory(destPath);
        //            DirectoryInfo[] dirs = dir.GetDirectories();
        //            FileInfo[] files = dir.GetFiles();

        //            foreach (FileInfo file in files)
        //            {
        //                string temppath = Path.Combine(destPath, file.Name);
        //                file.CopyTo(temppath, true);
        //            }

        //            foreach (DirectoryInfo subdir in dirs)
        //            {
        //                string temppath = Path.Combine(destPath, subdir.Name);
        //                TransferDir(subdir.FullName, temppath);
        //            }
        //            new DirectoryInfo(pathname).Delete(true);
        //        }
        //        else
        //        {
        //            return "550 Directory Not Found";
        //        }

        //        return "250 Requested file action okay, completed";
        //    }

        //    return "550 Directory Not Found";
        //}

        private string CreateDir(string pathname)
        {
            pathname = NormalizeFilename(pathname);

            if (pathname != null)
            {
                if (!Directory.Exists(pathname))
                {
                    Directory.CreateDirectory(pathname);
                }
                else
                {
                    return "550 Directory already exists";
                }

                return "250 Requested file action okay, completed";
            }

            return "550 Directory Not Found";
        }
        private string Rename(string renameFrom, string renameTo)
        {
            if (string.IsNullOrWhiteSpace(renameFrom) || string.IsNullOrWhiteSpace(renameTo))
            {
                return "450 Requested file action not taken";
            }

            renameFrom = NormalizeFilename(renameFrom);
            renameTo = NormalizeFilename(renameTo);

            if (renameFrom != null && renameTo != null)
            {
                if (File.Exists(renameFrom))
                {
                    try
                    {
                        File.Move(renameFrom, renameTo);
                    }
                    catch (Exception)
                    {
                        return "450 Requested file action not taken";
                    }
                }
                else if (Directory.Exists(renameFrom))
                {
                    try
                    {
                        Directory.Move(renameFrom, renameTo);
                    }
                    catch (Exception)
                    {
                        return "450 Requested file action not taken";
                    }
                }
                else
                {
                    return "450 Requested file action not taken";
                }

                return "250 Requested file action okay, completed";
            }

            return "450 Requested file action not taken";
        }

        //private string Auth(string authMode)
        //{
        //    if (authMode == "TLS")
        //    {
        //        return "234 Enabling TLS Connection";
        //    }
        //    else
        //    {
        //        return "504 Unrecognized AUTH mode";
        //    }
        //}
        #endregion

        private void DoList(IAsyncResult result)
        {
            if (_dataConnectionType == DataConnectionType.Active)
            {
                _dataClient.EndConnect(result);
            }
            else
            {
                _dataClient = _passiveListener.EndAcceptTcpClient(result);
            }

            string pathname = (string)result.AsyncState; //получаем путь

            using (NetworkStream dataStream = _dataClient.GetStream()) //объект NetworkStream для передачи данных 
            {
                _dataReader = new StreamReader(dataStream, Encoding.UTF8);
                _dataWriter = new StreamWriter(dataStream, Encoding.UTF8);

                try
                {
                    IEnumerable<string> directories = Directory.EnumerateDirectories(pathname); //Получаем папки

                    foreach (string dir in directories)
                    {
                        DirectoryInfo d = new DirectoryInfo(dir);
                        string date = d.LastWriteTime.Year.ToString().Substring(2, 2) + "-" +
                                        (d.LastWriteTime.Month > 9 ? d.LastWriteTime.Month.ToString() : "0" + d.LastWriteTime.Month.ToString()) + "-" +
                                        (d.LastWriteTime.Day > 9 ? d.LastWriteTime.Day.ToString() : "0" + d.LastWriteTime.Day.ToString()) + "  " +
                                        d.LastWriteTime.ToString("h:mm:ss.ff t").Substring(0, 4) + (d.LastWriteTime.Hour > 12 ? "PM" : "AM");
                        string line = String.Format("{0}       <DIR>     {1}", date, d.Name);

                        _dataWriter.WriteLine(line);
                        _dataWriter.Flush();

                    }
                }
                catch (UnauthorizedAccessException)
                {
                    _dataClient.Close();
                    _dataClient = null;

                    _controlWriter.WriteLine("550 Access denied or file/folder not found!");
                    _controlWriter.Flush();
                    return;
                }

                IEnumerable<string> files = Directory.EnumerateFiles(pathname); //Получаем файлы

                foreach (string file in files)
                {
                    FileInfo f = new FileInfo(file);
                    string date = f.LastWriteTime.Year.ToString().Substring(2, 2) + "-" +
                                 (f.LastWriteTime.Month > 9 ? f.LastWriteTime.Month.ToString() : "0" + f.LastWriteTime.Month.ToString()) + "-" +
                                 (f.LastWriteTime.Day > 9 ? f.LastWriteTime.Day.ToString() : "0" + f.LastWriteTime.Day.ToString()) + "  " +

                                  f.LastWriteTime.ToString("h:mm:ss.ff t").Substring(0, 4) + (f.LastWriteTime.Hour > 12 ? "PM" : "AM");

                    string line = String.Format("{0}     {1}", date, f.Name);
                    _dataWriter.WriteLine(line);
                    _dataWriter.Flush();

                }

                _dataClient.Close();
                _dataClient = null;

                _controlWriter.WriteLine("226 Transfer complete");
                _controlWriter.Flush();
            }
        }

        private bool IsPathValid(string pathname)
        {
            return pathname.StartsWith(_currentUser.UserRoot);
        }

        private string NormalizeFilename(string path)
        {
            if (path == null)
            {
                path = string.Empty;
            }

            if (path == "/")
            {
                return _root;
            }
            else if (path.StartsWith("/"))
            {
                path = path.Replace("/", "\\");
                path = _root + path;
            }
            else
            {
                path = path.Replace("/", "\\");
                path = _currentDirectory + "\\" + path;
            }
            //else if (path.StartsWith("/"))
            //{
            //    path = new FileInfo(Path.Combine(_root, path.Substring(1))).FullName;
            //}
            //else
            //{
            //    path = new FileInfo(Path.Combine(_currentDirectory, path)).FullName;
            //}

            return IsPathValid(path) ? path : null;
        }

        //private bool HasAccessToDirectory(string path)
        //{
        //    if (path.StartsWith(_currentUser.UserRoot))
        //    {
        //        DirectoryInfo Info = new DirectoryInfo(path);

        //        if (Info.Exists)
        //        {
        //            int index1 = _currentUser.UserRoot.Length - 1;
        //            int index2 = path.IndexOf("private");
        //            if (index2 == -1)
        //                return true;
        //            if (index2 < index1)
        //                return true;
        //            string UserName = path.Substring(index1 + 2, index2 + 1);
        //            if (UserName == _currentUser.UserName)
        //                return true;
        //            return false;
        //        }
        //        else
        //            return false;
        //    }
        //    else
        //        return false;

        //}

        private void DoDataConnectionOperation(IAsyncResult result)
        {
            if (_dataConnectionType == DataConnectionType.Active)
            {
                _dataClient.EndConnect(result);
            }
            else
            {
                _dataClient = _passiveListener.EndAcceptTcpClient(result);
            }

            string pathname = (string)result.AsyncState;
            try
            {
                using (NetworkStream dataStream = _dataClient.GetStream())
                {
                    SaveFile(dataStream, 4096, pathname);
                }
                _dataClient.Close();
                _dataClient = null;

                _controlWriter.WriteLine("226 Closing data connection, file transfer successful");
                _controlWriter.Flush();
            }
            catch (Exception ex)
            {
                _dataClient.Close();
                _dataClient = null;
                File.Delete(pathname);
                _controlWriter.WriteLine(ex.Message);
                _controlWriter.Flush();
            }

        }

        private void SaveFile(Stream dataStream, int bufferSize, string pathname)
        {
            long bytes = 0;
            using (FileStream fs = new FileStream(pathname, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
            {
                bytes = CopyStream(dataStream, fs, 4096);
            }
        }

        private string NormalizeDateTime()
        {
            string CurDateTime = DateTime.Now.ToString("u");
            string[] CurDateTimeArr = CurDateTime.Split(new char[] { '-', ':' }, StringSplitOptions.RemoveEmptyEntries);
            CurDateTime = String.Empty;
            foreach (string Elem in CurDateTimeArr)
            {
                CurDateTime += "_" + Elem;
            }
            return CurDateTime;
        }

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_controlClient != null)
                    {
                        _controlClient.Close();
                    }

                    if (_dataClient != null)
                    {
                        _dataClient.Close();
                    }

                    if (_controlStream != null)
                    {
                        _controlStream.Close();
                    }

                    if (_controlReader != null)
                    {
                        _controlReader.Close();
                    }

                    if (_controlWriter != null)
                    {
                        _controlWriter.Close();
                    }
                }
            }

            _disposed = true;
        }

        #endregion
    }
}
