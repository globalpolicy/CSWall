/**
 * CSWall 3
 * A program to periodically change desktop wallpaper using images from 500px.com
 * Author : s0ft
 * Contact : yciloplabolg@gmail.com
 * Blog : c0dew0rth.blogspot.com
 * Date/Time : 2017 April 28 - 10:28 PM
**/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using System.Net;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Reflection;

namespace CSWall
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region declarations
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
        private const int SPI_SETDESKWALLPAPER = 20;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDWININICHANGE = 0x02;

        private System.Threading.Timer timer;
        private static List<string> checkedcategories;
        private static Random rnd = new Random();
        private static int totalpages = 0;  //total pages available for the current category combination
        private static bool firstrun = true; //flag for timer (helps in deciding page numbers)
        private static string savepath; //currently saved image file path
        private string imagename; //currently saved image name
        private string currentlyshowingwallpapername; //currently showing wallpaper name
        private string currentlyshowingwallpaperpath; //currently shownig wallpaper storage location
        private System.Windows.Forms.NotifyIcon notifyicon;
        private object imagenamelock = new object(); //as an interthread lock token for controlling access to imagename and save file path
        private object filedownloaddeletelock = new object();
        private object onethreadatatimelock = new object(); //for when the timer executes the same method in different threads
        #endregion

        public MainWindow()
        {
            InitializeComponent();
        }

        private void checkBoxTimerOn_Checked(object sender, RoutedEventArgs e)
        {
            #region clear all existing temp images
            try
            {
                foreach (string fpth in Directory.EnumerateFiles(Path.GetTempPath(), "*.jpeg"))
                {
                    File.Delete(fpth);
                }
            }
            catch (Exception ex)
            {
                Debug.Print("Exception while deleting temp images\n" + ex.Message);
            }
            #endregion
            buttonChangeNow.IsEnabled = false;
            gathercheckedboxlist(); //gather what categories are checked
            firstrun = true; //reset the timer first run flag
            timer = new System.Threading.Timer(new System.Threading.TimerCallback(timermethod), null, 0, Convert.ToInt32(textBoxTimerInterval.Text) * 1000);
        }

        private async void timermethod(object obj)
        {
            try
            {
                if (checkedcategories.Count == 0) return;
                string _hpx1, xcsrftoken;

                #region obtain _hpx1 Cookie and X-CSRF Token
                using (HttpClientHandler httpclienthandler = new HttpClientHandler())
                {
                    HttpClient httpclient = new HttpClient(httpclienthandler);
                    HttpResponseMessage firstresponse = await httpclient.GetAsync("https://500px.com/popular");
                    var cookies = httpclienthandler.CookieContainer.GetCookies(new Uri("https://500px.com/popular"));
                    _hpx1 = cookies["_hpx1"].Value;
                    string firstresponsestring = await firstresponse.Content.ReadAsStringAsync();
                    int tmp1 = firstresponsestring.IndexOf("<meta name=\"csrf-token\" content=\"") + "<meta name=\"csrf-token\" content=\"".Length;
                    string tmp = firstresponsestring.Substring(tmp1);
                    int tmp2 = tmp.IndexOf("\" />");
                    xcsrftoken = tmp.Substring(0, tmp2);
                }
                #endregion

                #region process categories into URL
                string processedCategories = "";
                checkedcategories.ForEach((string category) =>
                {
                    if (category.IndexOf(" ") >= 0)
                    {
                        category = category.Replace(" ", "%20");
                    }
                    processedCategories += category + "%2C";
                });
                processedCategories = processedCategories.Substring(0, processedCategories.Length - 3);
                #endregion

                string apiURLBase = "https://api.500px.com/v1/photos";
                string url1 = apiURLBase + "?rpp=50&feature=popular&image_size=1080%2C1600%2C2048&only=" + processedCategories;
                if (totalpages != 0 && firstrun == false)
                {
                    int randompagenum = rnd.Next(1, totalpages);
                    url1 = url1 + "&page=" + randompagenum;
                }

                #region get the images for the selected categories
                JToken jtoken;
                using (HttpClientHandler handler = new HttpClientHandler())
                {
                    handler.UseCookies = true;
                    handler.CookieContainer.Add(new System.Net.Cookie("_hpx1", _hpx1) { Domain = "api.500px.com" });
                    using (HttpClient client = new HttpClient(handler))
                    {
                        client.DefaultRequestHeaders.Add("X-CSRF-Token", xcsrftoken);
                        HttpResponseMessage goodresponse = await client.GetAsync(url1);
                        string goodresponsestr = await goodresponse.Content.ReadAsStringAsync();
                        jtoken = JObject.Parse(goodresponsestr);
                    }
                }
                firstrun = false;
                totalpages = (int)jtoken["total_pages"]; //total pages of images available for this category combination
                int imagesperpage = ((JArray)jtoken["photos"]).Count; //sometimes, there may not be the requested for(50) images per page
                #endregion

                if (imagesperpage == 0)
                //true iff the wrong page number has been requested for the current category combination
                //happens if categories were changed between clicking the "Change" button
                {
                    firstrun = true; //next time, no page number request will be sent and fresh page count be obtained
                    Debug.Print("No image in requested page for this category combo!");
                    timermethod(null); //recall this method, this time, corrected for firstrun
                    return;
                }

                lock (onethreadatatimelock)
                {
                    #region select a random image from the current page and set wallpaper
                    #region choose a random photo and ready its download URL
                    int randomindex = rnd.Next(0, imagesperpage);
                    lock (imagenamelock) //threadwise critical code below
                    {
                        imagename = (string)jtoken["photos"][randomindex]["name"];
                        savepath = Path.GetTempPath() + Convert.ToBase64String(Encoding.UTF8.GetBytes(imagename)) + ".jpeg";
                    }
                    string imgurl = (string)jtoken["photos"][randomindex]["images"][2]["url"];
                    #endregion
                    #region update textbox
                    textBox.Dispatcher.BeginInvoke(new Action(() => { textBox.Text = "Coming up :\n" + imagename + "\n" + imgurl; }));
                    #endregion
                    #region download and set wallpaper
                    using (WebClient webcl = new WebClient())
                    {
                        lock (filedownloaddeletelock) //critical code below
                        {
                            webcl.DownloadFile(imgurl, savepath);
                            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, savepath, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
                            currentlyshowingwallpapername = imagename;
                            currentlyshowingwallpaperpath = savepath;
                        }
                    }
                    #endregion
                    #region update textbox
                    textBox.Dispatcher.BeginInvoke(new Action(() => { textBox.Text = imagename + "\n" + imgurl; }));
                    Debug.Print(imagename + "\n" + imgurl);
                    #endregion

                    #endregion
                }
            }
            catch (Exception ex)
            {
                Debug.Print("Something went wrong.\n" + ex.Message + "\n" + ex.Source);
                await textBox.Dispatcher.BeginInvoke(new Action(() => { textBox.Text = "Something went wrong.\n" + ex.Message + "\n" + ex.Source; }));
            }
        }

        private void checkBoxTimerOn_Unchecked(object sender, RoutedEventArgs e)
        {
            timer.Dispose();
            buttonChangeNow.IsEnabled = true;
        }

        private void buttonChangeNow_Click(object sender, RoutedEventArgs e)
        {
            new System.Threading.Thread(() =>
                {
                    buttonChangeNow.Dispatcher.Invoke(() => buttonChangeNow.IsEnabled = false);
                    System.Threading.Thread.Sleep(5000);
                    buttonChangeNow.Dispatcher.Invoke(() => buttonChangeNow.IsEnabled = true);
                }
            ).Start();
            gathercheckedboxlist();
            new System.Threading.Thread(new System.Threading.ParameterizedThreadStart(timermethod)).Start(null);
        }

        private void gathercheckedboxlist()
        {
            checkedcategories = new List<string>();
            foreach (Control control in gridofCheckboxes.Children)
            {
                if (control is CheckBox)
                {
                    if (((CheckBox)control).IsChecked == true)
                    {
                        if (((CheckBox)control).Tag == null)
                        {
                            checkedcategories.Add(((CheckBox)control).Content.ToString());
                        }
                        else
                        {
                            checkedcategories.Add(((CheckBox)control).Tag.ToString());
                        }

                    }

                }
            }
        }

        private void buttonSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string tmp = "";

                if (!File.Exists(Directory.GetCurrentDirectory() + "\\" + processImageName(currentlyshowingwallpapername) + ".jpeg"))
                    tmp = Directory.GetCurrentDirectory() + "\\" + processImageName(currentlyshowingwallpapername) + ".jpeg";
                else
                    tmp = Directory.GetCurrentDirectory() + "\\" + processImageName(currentlyshowingwallpapername) + "_" + DateTime.Now.Ticks + ".jpeg";
                File.Copy(currentlyshowingwallpaperpath, tmp);

                #region update UI to show the saved message
                new System.Threading.Thread(new System.Threading.ParameterizedThreadStart(
                    delegate (object pth)
                    {
                        string path = (string)pth;

                        this.Dispatcher.Invoke(() =>
                        {
                            textBox.Text += "\nSaved to " + path;
                        });

                    }
                    )).Start(tmp);
                #endregion
                Debug.Print("saved to {0}", tmp);
            }
            catch (Exception ex)
            {
                Debug.Print(ex.Message);
            }

        }

        private string processImageName(string illegalname)
        {
            string legalname;
            legalname = illegalname.Replace("\\", "").Replace("\"", "").Replace(":", "").Replace("<", "").Replace(">", "").Replace("/", "").Replace("|", "").Replace("?", "").Replace("*", "");
            return legalname;
        }

        private void buttonAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Author : s0ft\nContact : yciloplabolg@gmail.com\nBlog : c0dew0rth.blogspot.com", "CSWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void checkBoxSelectAll_Checked(object sender, RoutedEventArgs e)
        {
            foreach (Control ctrl in gridofCheckboxes.Children)
            {
                if (ctrl is CheckBox)
                {
                    ((CheckBox)ctrl).IsChecked = true;
                }
            }
        }

        private void checkBoxSelectAll_Unchecked(object sender, RoutedEventArgs e)
        {
            foreach (Control ctrl in gridofCheckboxes.Children)
            {
                if (ctrl is CheckBox)
                {
                    ((CheckBox)ctrl).IsChecked = false;
                }
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide();
                notifyicon = new System.Windows.Forms.NotifyIcon();
                notifyicon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
                notifyicon.Visible = true;


                notifyicon.MouseMove += (somex, somey) =>
                {
                    try
                    {
                        if (currentlyshowingwallpapername != null)
                        {
                            notifyicon.Text = "Current wallpaper image name is : \n" + currentlyshowingwallpapername;
                        }
                        else
                        {
                            notifyicon.Text = "Activate timer for wallpaper slideshow.";
                        }
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        //if notifyicon.Text is >64 characters, ArgumentOutOfRangeException thrown
                        Debug.Print(ex.Message);
                        notifyicon.Text = "CSWall\nWallpaper name too long.";
                    }
                };


                notifyicon.DoubleClick += (somex, somey) =>
                {
                    notifyicon.Dispose();
                    this.Show();
                    this.WindowState = WindowState.Normal;
                };


            }
        }

    }
}
