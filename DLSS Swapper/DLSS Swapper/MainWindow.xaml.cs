﻿using AsyncAwaitBestPractices;
using DLSS_Swapper.Data;
using DLSS_Swapper.Extensions;
using DLSS_Swapper.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MvvmHelpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DLSS_Swapper
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public Grid AppTitleBar => appTitleBar;

        public static NavigationView NavigationView;

        public ObservableRangeCollection<DLSSRecord> CurrentDLSSRecords { get; } = new ObservableRangeCollection<DLSSRecord>();

        public MainWindow()
        {
            Title = "DLSS Swapper";
            this.InitializeComponent();
            NavigationView = MainNavigationView;

            MainNavigationView.RequestedTheme = (ElementTheme)Settings.AppTheme;
        }

        void MainNavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            //FrameNavigationOptions navOptions = new FrameNavigationOptions();
            //navOptions.TransitionInfoOverride = args.RecommendedNavigationTransitionInfo;

            if (args.InvokedItem is String invokedItem)
            {
                GoToPage(invokedItem);
            }
        }

        void GoToPage(string page)
        {
            Type pageType = null;

            if (page == "Games")
            {
                pageType = typeof(GameGridPage);
            }
            else if (page == "Library")
            {
                pageType = typeof(LibraryPage);
            }
            else if (page == "Settings")
            {
                pageType = typeof(SettingsPage);
            }
            else if (page == "InitialLoading")
            {
                pageType = typeof(InitialLoadingPage);
            }

            foreach (NavigationViewItem navigationViewItem in MainNavigationView.MenuItems)
            {
                if (navigationViewItem.Tag.ToString() == page)
                {
                    MainNavigationView.SelectedItem = navigationViewItem;
                    break;
                }
            }

            if (pageType != null)
            {
                ContentFrame.Navigate(pageType);
            }
        }

        async void MainNavigationView_Loaded(object sender, RoutedEventArgs e)
        {
            var gitHubUpdater = new Data.GitHub.GitHubUpdater();
            // If this is a new build, fetch updates to display to the user.
            Task<Data.GitHub.GitHubRelease> releaseNotesTask = null;
            if (CommunityToolkit.WinUI.Helpers.SystemInformation.Instance.IsAppUpdated)
            {
                var currentAppVersion = Windows.ApplicationModel.Package.Current.Id.Version;
                releaseNotesTask = gitHubUpdater.GetReleaseFromTag($"v{currentAppVersion.Major}.{currentAppVersion.Minor}.{currentAppVersion.Build}.{currentAppVersion.Revision}"); 
            }

#if !RELEASE_WINDOWSSTORE
            // If this is a GitHub build check if there is a new version.
            // Lazy blocks to allow mul
            Task<Data.GitHub.GitHubRelease> newUpdateTask = gitHubUpdater.CheckForNewGitHubRelease();
#endif


            // Load from cache, or download if not found.
            var loadDlssRecrodsTask = LoadDLSSRecordsAsync();
            var loadImportedDlssRecords = LoadImportedDLSSRecordsAsync();


            var didLoadDlssRecords = await loadDlssRecrodsTask;
            if (didLoadDlssRecords == false)
            {
                var dialog = new ContentDialog()
                {
                    Title = "Error",
                    CloseButtonText = "Close",
                    PrimaryButtonText = "Github Issues",
                    Content = @"We were unable to load dlss_records.json from your computer or from the internet. 

If this keeps happening please file an report in our issue tracker on Github.

DLSS Swapper will close now.",
                    XamlRoot = MainNavigationView.XamlRoot,
                };
                var response = await dialog.ShowAsync();
                if (response == ContentDialogResult.Primary)
                {
                    await Launcher.LaunchUriAsync(new Uri("https://github.com/beeradmoore/dlss-swapper/issues"));
                }

                Close();
            }

            await loadImportedDlssRecords;

            FilterDLSSRecords();
            //await App.CurrentApp.LoadLocalRecordsAsync();
            App.CurrentApp.LoadLocalRecords();

            // We are now ready to show the games list.
            LoadingStackPanel.Visibility = Visibility.Collapsed;
            GoToPage("Games");

            if (releaseNotesTask != null)
            {
                await releaseNotesTask;
                if (releaseNotesTask.Result != null)
                {
                    gitHubUpdater?.DisplayWhatsNewDialog(releaseNotesTask.Result, MainNavigationView);
                }
            }

#if !RELEASE_WINDOWSSTORE
            await newUpdateTask;
            if (newUpdateTask.Result != null)
            {
                if (gitHubUpdater.HasPromptedBefore(newUpdateTask.Result) == false)
                {
                    await gitHubUpdater.DisplayNewUpdateDialog(newUpdateTask.Result, MainNavigationView);
                }
            }       
#endif
        }

        /// <summary>
        /// 
        /// </summary>
        internal void FilterDLSSRecords()
        {
            var newDlssRecordsList = new List<DLSSRecord>();
            if (Settings.AllowUntrusted)
            {
                newDlssRecordsList.AddRange(App.CurrentApp.DLSSRecords?.Stable);
                newDlssRecordsList.AddRange(App.CurrentApp.ImportedDLSSRecords);
            }
            else
            {
                newDlssRecordsList.AddRange(App.CurrentApp.DLSSRecords?.Stable.Where(x => x.IsSignatureValid == true));
                newDlssRecordsList.AddRange(App.CurrentApp.ImportedDLSSRecords.Where(x => x.IsSignatureValid == true));
            }

            if (Settings.AllowExperimental)
            {
                if (Settings.AllowUntrusted)
                {
                    newDlssRecordsList.AddRange(App.CurrentApp.DLSSRecords?.Experimental);
                }
                else
                {
                    newDlssRecordsList.AddRange(App.CurrentApp.DLSSRecords?.Experimental.Where(x => x.IsSignatureValid == true));
                }
            }


            newDlssRecordsList.Sort();
            CurrentDLSSRecords.Clear();
            CurrentDLSSRecords.AddRange(newDlssRecordsList);
        }

        /// <summary>
        /// Attempts to load DLSS records from disk or from the web depending what happened.
        /// </summary>
        /// <returns>True if we expect there are now valid DLSS records loaded into memory.</returns>
        async Task<bool> LoadDLSSRecordsAsync()
        {
            // Only auto check for updates once every 12 hours.
            var timeSinceLastUpdate = DateTimeOffset.Now - Settings.LastRecordsRefresh;
            if (timeSinceLastUpdate.TotalHours > 12)
            {
                var didUpdate = await UpdateDLSSRecordsAsync();
                if (didUpdate)
                {
                    return true;
                }
            }

            // If we were unable to auto-load lets try load cached.
            var storageFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
            try
            {
                var dlssRecordsFile = await storageFolder.GetFileAsync("dlss_records.json");
                using (var stream = await dlssRecordsFile.OpenSequentialReadAsync())
                {
                    var items = await JsonSerializer.DeserializeAsync<DLSSRecords>(stream.AsStreamForRead());
                    UpdateDLSSRecordsList(items);
                    //await UpdateDLSSRecordsListAsync(items);
                }
                return true;
            }
            catch (FileNotFoundException)
            {
                // If the file was not found we will download fresh records list (possibly for a second time)
                return await UpdateDLSSRecordsAsync();
            }
            catch (Exception err)
            {
                Console.WriteLine($"LoadDLSSRecords Error: {err.Message}");
                return false;
            }
        }

        internal async Task LoadImportedDLSSRecordsAsync()
        {
            var storageFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
            try
            {
                var importedDlssRecordsFile = await storageFolder.GetFileAsync("imported_dlss_records.json");
                using (var stream = await importedDlssRecordsFile.OpenSequentialReadAsync())
                {
                    var items = await JsonSerializer.DeserializeAsync<List<DLSSRecord>>(stream.AsStreamForRead());
                    UpdateImportedDLSSRecordsList(items);
                }
            }
            catch (FileNotFoundException)
            {
                // NOOP.
                return;
            }
            catch (Exception err)
            {
                Debug.WriteLine($"LoadDLSSRecords Error: {err.Message}");
                return;
            }
        }

        internal void UpdateDLSSRecordsList(DLSSRecords dlssRecords)
        {
            App.CurrentApp.DLSSRecords.Stable.Clear();
            App.CurrentApp.DLSSRecords.Stable.AddRange(dlssRecords.Stable);

            App.CurrentApp.DLSSRecords.Experimental.Clear();
            App.CurrentApp.DLSSRecords.Experimental.AddRange(dlssRecords.Experimental);
        }

        internal void UpdateImportedDLSSRecordsList(List<DLSSRecord> localDlssRecords)
        {
            App.CurrentApp.ImportedDLSSRecords.Clear();
            App.CurrentApp.ImportedDLSSRecords.AddRange(localDlssRecords);
        }

        /// <summary>
        /// Attempts to load dlss_records.json from dlss-archive.
        /// </summary>
        /// <returns>True if the dlss recrods manifest was downloaded and saved successfully</returns>
        internal async Task<bool> UpdateDLSSRecordsAsync()
        {
            var url = "https://raw.githubusercontent.com/beeradmoore/dlss-archive/main/dlss_records.json";

            try
            {
                using (var memoryStream = new MemoryStream())
                {
                    // TODO: Check how quickly this takes to timeout if there is no internet connection. Consider 
                    // adding a "fast UpdateDLSSRecords" which will quit early if we were unable to load in 10sec 
                    // which would then fall back to loading local.                    
                    using (var stream = await App.CurrentApp.HttpClient.GetStreamAsync(url))
                    {
                        await stream.CopyToAsync(memoryStream);
                    }

                    memoryStream.Position = 0;

                    var items = await JsonSerializer.DeserializeAsync<DLSSRecords>(memoryStream);

                    UpdateDLSSRecordsList(items);
                    //await UpdateDLSSRecordsListAsync(items);

                    memoryStream.Position = 0;

                    var storageFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    try
                    {
                        var dlssRecordsFile = await storageFolder.CreateFileAsync("dlss_records.json", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                        using (var writeStream = await dlssRecordsFile.OpenStreamForWriteAsync())
                        {
                            await memoryStream.CopyToAsync(writeStream);
                        }
                        // Update settings for auto refresh.
                        Settings.LastRecordsRefresh = DateTime.Now;
                        return true;
                    }
                    catch (Exception err)
                    {
                        Console.WriteLine($"UpdateDLSSRecords Error: {err.Message}");
                    }
                }
            }
            catch (Exception err)
            {
                Console.WriteLine($"UpdateDLSSRecords Error: {err.Message}");
            }

            return false;
        }
    }
}
