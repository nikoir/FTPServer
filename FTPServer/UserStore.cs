using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FTPServer
{
    class UserStore
    {
        private string _userRoot;
        private string _userFolder;
        private string _userPrivateFolder;
        private string _userPublicFolder;
        private string _userName;

        public string UserRoot
        {
            get
            {
                return _userRoot;
            }
        }

        public string UserFolder
        {
            get
            {
                return _userFolder;
            }
        }

        public string UserPrivateFolder
        {
            get
            {
                return _userPrivateFolder;
            }
        }

        public string UserPublicFolder
        {
            get
            {
                return _userPublicFolder;
            }
        }

        public string UserName
        {
            get
            {
                return _userName;
            }
        }

        public UserStore(string serverRoot, string userName, string userGroup)
        {
            Path.GetFullPath(serverRoot); //Если путь некорректный - то будет Exception
            if (!Directory.Exists(serverRoot))
                Directory.CreateDirectory(serverRoot);

            _userRoot = Path.Combine(serverRoot, userGroup);
            Path.GetFullPath(_userRoot);
            if (!Directory.Exists(_userRoot))
                Directory.CreateDirectory(_userRoot);

            _userFolder = Path.Combine(_userRoot, userName);
            Path.GetFullPath(_userFolder);
            _userName = userName;
            if (!Directory.Exists(_userFolder))
                Directory.CreateDirectory(_userFolder);

            _userPrivateFolder = Path.Combine(_userFolder, "private");
            if (!Directory.Exists(_userPrivateFolder))
                Directory.CreateDirectory(_userPrivateFolder);

            _userPublicFolder = Path.Combine(_userFolder, "public");
            if (!Directory.Exists(_userPublicFolder))
                Directory.CreateDirectory(_userPublicFolder);
        }

        public static string GetUserGroup(string UserName, string Password)
        {
            string[] Users = File.ReadAllLines("Users.csv");
            string[] UserData;
            foreach (string User in Users)
            {
                UserData = User.Split(new char[]{';'}, StringSplitOptions.RemoveEmptyEntries);
                if (UserData[0] == UserName && UserData[1] == Password)
                    return UserData[2];
            }
            return null;
        }

        public static void Create(string UserName, string Password, string Group)
        {
            using(StreamWriter writer = new StreamWriter("Users.csv"))
            {
                writer.WriteLine(String.Format("{0};{1};{2}", UserName, Password, Group));
            }
        }
    }
}
