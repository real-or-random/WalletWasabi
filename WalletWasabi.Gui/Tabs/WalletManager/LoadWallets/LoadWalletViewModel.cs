using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AvalonStudio.Extensibility;
using AvalonStudio.Shell;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nito.AsyncEx;
using ReactiveUI;
using Splat;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Gui.Controls.WalletExplorer;
using WalletWasabi.Gui.Dialogs;
using WalletWasabi.Gui.Helpers;
using WalletWasabi.Gui.Models;
using WalletWasabi.Gui.Models.StatusBarStatuses;
using WalletWasabi.Gui.ViewModels;
using WalletWasabi.Gui.ViewModels.Validation;
using WalletWasabi.Helpers;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Logging;
using WalletWasabi.Models;

namespace WalletWasabi.Gui.Tabs.WalletManager.LoadWallets
{
	public class LoadWalletViewModel : CategoryViewModel
	{
		private ObservableCollection<LoadWalletEntry> _wallets;
		private string _password;
		private LoadWalletEntry _selectedWallet;
		private bool _isWalletSelected;
		private bool _isWalletOpened;
		private bool _canLoadWallet;
		private bool _canTestPassword;
		private bool _isBusy;
		private bool _isHardwareBusy;
		private string _loadButtonText;
		private bool _isHwWalletSearchTextVisible;

		public LoadWalletViewModel(WalletManagerViewModel owner, LoadWalletType loadWalletType)
			: base(loadWalletType == LoadWalletType.Password ? "Test Password" : loadWalletType == LoadWalletType.Desktop ? "Load Wallet" : "Hardware Wallet")
		{
			Global = Locator.Current.GetService<Global>();

			Owner = owner;
			Password = "";
			LoadWalletType = loadWalletType;
			Wallets = new ObservableCollection<LoadWalletEntry>();
			IsHwWalletSearchTextVisible = false;

			this.WhenAnyValue(x => x.SelectedWallet)
				.Subscribe(_ => TrySetWalletStates());

			this.WhenAnyValue(x => x.IsWalletOpened)
				.Subscribe(_ => TrySetWalletStates());

			this.WhenAnyValue(x => x.IsBusy)
				.Subscribe(_ => TrySetWalletStates());

			LoadCommand = ReactiveCommand.CreateFromTask(LoadWalletAsync, this.WhenAnyValue(x => x.CanLoadWallet));
			TestPasswordCommand = ReactiveCommand.CreateFromTask(LoadKeyManagerAsync, this.WhenAnyValue(x => x.CanTestPassword));
			OpenFolderCommand = ReactiveCommand.Create(OpenWalletsFolder);

			Observable
				.Merge(LoadCommand.ThrownExceptions)
				.Merge(TestPasswordCommand.ThrownExceptions)
				.Merge(OpenFolderCommand.ThrownExceptions)
				.ObserveOn(RxApp.TaskpoolScheduler)
				.Subscribe(ex =>
				{
					Logger.LogError(ex);
					NotificationHelpers.Error(ex.ToUserFriendlyString());
				});

			SetLoadButtonText();
		}

		public LoadWalletType LoadWalletType { get; }
		public bool IsPasswordRequired => LoadWalletType == LoadWalletType.Password;
		public bool IsHardwareWallet => LoadWalletType == LoadWalletType.Hardware;
		public bool IsDesktopWallet => LoadWalletType == LoadWalletType.Desktop;

		public bool IsHwWalletSearchTextVisible
		{
			get => _isHwWalletSearchTextVisible;
			set => this.RaiseAndSetIfChanged(ref _isHwWalletSearchTextVisible, value);
		}

		public ObservableCollection<LoadWalletEntry> Wallets
		{
			get => _wallets;
			set => this.RaiseAndSetIfChanged(ref _wallets, value);
		}

		[ValidateMethod(nameof(ValidatePassword))]
		public string Password
		{
			get => _password;
			set => this.RaiseAndSetIfChanged(ref _password, value);
		}

		public LoadWalletEntry SelectedWallet
		{
			get => _selectedWallet;
			set => this.RaiseAndSetIfChanged(ref _selectedWallet, value);
		}

		public bool IsWalletSelected
		{
			get => _isWalletSelected;
			set => this.RaiseAndSetIfChanged(ref _isWalletSelected, value);
		}

		public bool IsWalletOpened
		{
			get => _isWalletOpened;
			set => this.RaiseAndSetIfChanged(ref _isWalletOpened, value);
		}

		public string LoadButtonText
		{
			get => _loadButtonText;
			set => this.RaiseAndSetIfChanged(ref _loadButtonText, value);
		}

		public bool CanLoadWallet
		{
			get => _canLoadWallet;
			set => this.RaiseAndSetIfChanged(ref _canLoadWallet, value);
		}

		public bool CanTestPassword
		{
			get => _canTestPassword;
			set => this.RaiseAndSetIfChanged(ref _canTestPassword, value);
		}

		public bool IsBusy
		{
			get => _isBusy;
			set => this.RaiseAndSetIfChanged(ref _isBusy, value);
		}

		public bool IsHardwareBusy
		{
			get => _isHardwareBusy;
			set
			{
				this.RaiseAndSetIfChanged(ref _isHardwareBusy, value);

				try
				{
					TrySetWalletStates();
				}
				catch (Exception ex)
				{
					Logger.LogInfo(ex);
				}
			}
		}

		public ReactiveCommand<Unit, Unit> LoadCommand { get; }
		public ReactiveCommand<Unit, KeyManager> TestPasswordCommand { get; }
		public ReactiveCommand<Unit, Unit> OpenFolderCommand { get; }
		private WalletManagerViewModel Owner { get; }

		private Global Global { get; }

		public ErrorDescriptors ValidatePassword() => PasswordHelper.ValidatePassword(Password);

		public void SetLoadButtonText()
		{
			var text = "Load Wallet";
			if (IsHardwareBusy)
			{
				text = "Waiting for Hardware Wallet...";
			}
			else if (IsBusy)
			{
				text = "Loading...";
			}
			else
			{
				// If the hardware wallet was not initialized, then make the button say Setup, not Load.
				// If pin is needed, then make the button say Send Pin instead.

				if (SelectedWallet?.HardwareWalletInfo != null)
				{
					if (!SelectedWallet.HardwareWalletInfo.IsInitialized())
					{
						text = "Setup Wallet";
					}

					if (SelectedWallet.HardwareWalletInfo.NeedsPinSent is true)
					{
						text = "Send PIN";
					}
				}
			}

			LoadButtonText = text;
		}

		public override void OnCategorySelected()
		{
			if (IsHardwareWallet)
			{
				return;
			}

			Wallets.Clear();
			Password = "";

			foreach (var wallet in Global.WalletManager
				.GetKeyManagers()
				.Where(x => !IsPasswordRequired || !x.IsWatchOnly) // If password isn't required then add the wallet, otherwise add only not watchonly wallets.
				.OrderByDescending(x => x.GetLastAccessTime())
				.Select(x => new LoadWalletEntry(x.WalletName)))
			{
				Wallets.Add(wallet);
			}

			TrySetWalletStates();

			if (!CanLoadWallet && Wallets.Count > 0)
			{
				NotificationHelpers.Warning("There is already an open wallet. Restart the application in order to open a different one.");
			}
		}

		public async Task<KeyManager> LoadKeyManagerAsync()
		{
			try
			{
				CanTestPassword = false;
				var password = Guard.Correct(Password); // Do not let whitespaces to the beginning and to the end.
				Password = ""; // Clear password field.

				var selectedWallet = SelectedWallet;
				if (selectedWallet is null)
				{
					NotificationHelpers.Warning("No wallet selected.");
					return null;
				}

				var walletName = selectedWallet.WalletName;
				if (IsHardwareWallet)
				{
					var client = new HwiClient(Global.Network);

					if (selectedWallet.HardwareWalletInfo is null)
					{
						NotificationHelpers.Warning("No hardware wallet detected.");
						return null;
					}

					if (!selectedWallet.HardwareWalletInfo.IsInitialized())
					{
						try
						{
							IsHardwareBusy = true;
							MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusType.SettingUpHardwareWallet);

							// Setup may take a while for users to write down stuff.
							using (var ctsSetup = new CancellationTokenSource(TimeSpan.FromMinutes(21)))
							{
								// Trezor T doesn't require interactive mode.
								if (selectedWallet.HardwareWalletInfo.Model == HardwareWalletModels.Trezor_T
								|| selectedWallet.HardwareWalletInfo.Model == HardwareWalletModels.Trezor_T_Simulator)
								{
									await client.SetupAsync(selectedWallet.HardwareWalletInfo.Model, selectedWallet.HardwareWalletInfo.Path, false, ctsSetup.Token);
								}
								else
								{
									await client.SetupAsync(selectedWallet.HardwareWalletInfo.Model, selectedWallet.HardwareWalletInfo.Path, true, ctsSetup.Token);
								}
							}

							MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusType.ConnectingToHardwareWallet);
							await EnumerateIfHardwareWalletsAsync();
						}
						finally
						{
							IsHardwareBusy = false;
							MainWindowViewModel.Instance.StatusBar.TryRemoveStatus(StatusType.SettingUpHardwareWallet, StatusType.ConnectingToHardwareWallet);
						}

						return await LoadKeyManagerAsync();
					}
					else if (selectedWallet.HardwareWalletInfo.NeedsPinSent is true)
					{
						await PinPadViewModel.UnlockAsync(selectedWallet.HardwareWalletInfo);

						var p = selectedWallet.HardwareWalletInfo.Path;
						var t = selectedWallet.HardwareWalletInfo.Model;
						await EnumerateIfHardwareWalletsAsync();
						selectedWallet = Wallets.FirstOrDefault(x => x.HardwareWalletInfo.Model == t && x.HardwareWalletInfo.Path == p);
						if (selectedWallet is null)
						{
							NotificationHelpers.Warning("Could not find the hardware wallet. Did you disconnect it?");
							return null;
						}
						else
						{
							SelectedWallet = selectedWallet;
						}

						if (!selectedWallet.HardwareWalletInfo.IsInitialized())
						{
							NotificationHelpers.Warning("Hardware wallet is not initialized.");
							return null;
						}

						if (selectedWallet.HardwareWalletInfo.NeedsPinSent is true)
						{
							NotificationHelpers.Warning("Hardware wallet needs a PIN to be sent.");
							return null;
						}
					}

					ExtPubKey extPubKey;
					var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
					try
					{
						MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusType.AcquiringXpubFromHardwareWallet);
						extPubKey = await client.GetXpubAsync(selectedWallet.HardwareWalletInfo.Model, selectedWallet.HardwareWalletInfo.Path, KeyManager.DefaultAccountKeyPath, cts.Token);
					}
					finally
					{
						cts?.Dispose();
						MainWindowViewModel.Instance.StatusBar.TryRemoveStatus(StatusType.AcquiringXpubFromHardwareWallet);
					}

					Logger.LogInfo("Hardware wallet was not used previously on this computer. Creating a new wallet file.");

					if (TryFindWalletByExtPubKey(extPubKey, out string wn))
					{
						walletName = wn;
					}
					else
					{
						var prefix = selectedWallet.HardwareWalletInfo is null ? "HardwareWallet" : selectedWallet.HardwareWalletInfo.Model.ToString();

						walletName = Global.WalletManager.WalletDirectories.GetNextWalletName(prefix);
						var path = Global.WalletManager.WalletDirectories.GetWalletFilePaths(walletName).walletFilePath;

						// Get xpub should had triggered passphrase request, so the fingerprint should be available here.
						if (!selectedWallet.HardwareWalletInfo.Fingerprint.HasValue)
						{
							await EnumerateIfHardwareWalletsAsync();
							selectedWallet = Wallets.FirstOrDefault(x => x.HardwareWalletInfo.Model == selectedWallet.HardwareWalletInfo.Model && x.HardwareWalletInfo.Path == selectedWallet.HardwareWalletInfo.Path);
						}
						if (!selectedWallet.HardwareWalletInfo.Fingerprint.HasValue)
						{
							throw new InvalidOperationException("Hardware wallet did not provide fingerprint.");
						}
						KeyManager.CreateNewHardwareWalletWatchOnly(selectedWallet.HardwareWalletInfo.Fingerprint.Value, extPubKey, path);
					}
				}

				KeyManager keyManager = Global.WalletManager.GetWalletByName(walletName).KeyManager;

				// Only check requirepassword here, because the above checks are applicable to loadwallet, too and we are using this function from load wallet.
				if (IsPasswordRequired)
				{
					if (PasswordHelper.TryPassword(keyManager, password, out string compatibilityPasswordUsed))
					{
						NotificationHelpers.Success("Correct password.");
						if (compatibilityPasswordUsed != null)
						{
							NotificationHelpers.Warning(PasswordHelper.CompatibilityPasswordWarnMessage);
						}

						keyManager.SetPasswordVerified();
					}
					else
					{
						NotificationHelpers.Error("Wrong password.");
						return null;
					}
				}
				else
				{
					if (keyManager.PasswordVerified == false)
					{
						Owner.SelectTestPassword();
						return null;
					}
				}

				return keyManager;
			}
			catch (Exception ex)
			{
				try
				{
					await EnumerateIfHardwareWalletsAsync();
				}
				catch (Exception ex2)
				{
					Logger.LogError(ex2);
				}

				// Initialization failed.
				NotificationHelpers.Error(ex.ToUserFriendlyString());
				Logger.LogError(ex);

				return null;
			}
			finally
			{
				CanTestPassword = IsWalletSelected;
			}
		}

		public async Task LoadWalletAsync()
		{
			try
			{
				IsBusy = true;

				var keyManager = await LoadKeyManagerAsync();
				if (keyManager is null)
				{
					return;
				}

				try
				{
					bool isSuccessful = await Global.WaitForInitializationCompletedAsync(CancellationToken.None);
					if (!isSuccessful)
					{
						return;
					}

					var wallet = await Task.Run(async () => await Global.WalletManager.StartWalletAsync(keyManager));
					// Successfully initialized.
					Owner.OnClose();
				}
				catch (Exception ex)
				{
					// Initialization failed.
					NotificationHelpers.Error(ex.ToUserFriendlyString());
					if (!(ex is OperationCanceledException))
					{
						Logger.LogError(ex);
					}
				}
			}
			finally
			{
				IsBusy = false;
			}
		}

		public void OpenWalletsFolder() => IoHelpers.OpenFolderInFileExplorer(Global.WalletManager.WalletDirectories.WalletsDir);

		private bool TrySetWalletStates()
		{
			try
			{
				if (SelectedWallet is null)
				{
					SelectedWallet = Wallets.FirstOrDefault();
				}

				IsWalletSelected = SelectedWallet != null;
				CanTestPassword = IsWalletSelected;

				if (Global.WalletManager.AnyWallet())
				{
					IsWalletOpened = true;
					CanLoadWallet = false;
				}
				else
				{
					IsWalletOpened = false;

					// If not busy loading.
					// And wallet is selected.
					// And no wallet is opened.
					CanLoadWallet = !IsBusy && IsWalletSelected;
				}

				SetLoadButtonText();
				return true;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex);
			}

			return false;
		}

		protected async Task EnumerateIfHardwareWalletsAsync()
		{
			if (!IsHardwareWallet)
			{
				return;
			}
			var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
			IsHwWalletSearchTextVisible = true;
			try
			{
				var client = new HwiClient(Global.Network);
				var devices = await client.EnumerateAsync(cts.Token);

				Wallets.Clear();
				foreach (var dev in devices)
				{
					var walletEntry = new LoadWalletEntry(dev);
					Wallets.Add(walletEntry);
				}
				TrySetWalletStates();
			}
			finally
			{
				IsHwWalletSearchTextVisible = false;
				cts.Dispose();
			}
		}

		private bool TryFindWalletByExtPubKey(ExtPubKey extPubKey, out string walletName)
		{
			walletName = Global.WalletManager.WalletDirectories
				.EnumerateWalletFiles(includeBackupDir: true)
				.FirstOrDefault(fi => KeyManager.TryGetExtPubKeyFromFile(fi.FullName, out ExtPubKey epk) && epk == extPubKey)
				?.Name;

			return walletName is { };
		}
	}
}
