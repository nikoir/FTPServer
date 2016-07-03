using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FTPServer
{
    class Program
    {
        static void Main(string[] args)
        {
            FTPServer server = new FTPServer();
            while (true)
            {
                server.Start();
                Console.ReadLine();
            }
        }
    }
}
