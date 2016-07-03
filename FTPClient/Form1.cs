using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FTPClient
{
    public partial class Form1 : Form
    {
        FTPClient client;
        ImageList imageList;
        string serverDir = String.Empty;
        FileStruct[] DirectoriesFiles;

        private void CreateImages()
        {
            imageList = new ImageList();
            Image FolderImage = Image.FromFile("folder.jpg");
            Image FileImage = Image.FromFile("file.png");
            imageList.Images.Add(FolderImage);
            imageList.Images.Add(FileImage);
            listView1.LargeImageList = imageList;
        }
        public Form1()
        {
            client = new FTPClient();
            InitializeComponent();
            CreateImages();
        }
        private void GetDirectoryContent(string path)
        {
            try
            {
                DirectoriesFiles = client.ListDirectory(path);
                if (listView1.Items.Count != 0)
                    listView1.Items.Clear();
                foreach (var DirectoryFile in DirectoriesFiles)
                {
                    listView1.Items.Add(DirectoryFile.Name);
                    if (DirectoryFile.IsDirectory)
                        listView1.Items[listView1.Items.Count - 1].ImageIndex = 0;
                    else
                        listView1.Items[listView1.Items.Count - 1].ImageIndex = 1;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            GetDirectoryContent("");
        }

        private void listView1_DoubleClick(object sender, EventArgs e)
        {
            if (listView1.SelectedItems.Count != 0)
            {
                int index = listView1.SelectedItems[0].Index;
                var DirectoryFile = DirectoriesFiles[index];
                if (DirectoryFile.IsDirectory)
                {
                    serverDir += ("/" + DirectoryFile.Name);
                    GetDirectoryContent(serverDir);
                }
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            int index = serverDir.LastIndexOf("/");
            if (index >= 0)
            {
                serverDir = serverDir.Substring(0, index);
            }
            GetDirectoryContent(serverDir);
        }
    }
}
