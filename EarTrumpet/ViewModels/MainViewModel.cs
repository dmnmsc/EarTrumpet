﻿using EarTrumpet.DataModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Timers;
using System.Windows;
using System.Windows.Controls;

namespace EarTrumpet.ViewModels
{
    public enum ViewState
    {
        NotReady,
        Hidden,
        Opening,
        Opening_CloseRequested,
        Open,
        Closing,
    }

    public class MainViewModel : BindableBase
    {
        public static MainViewModel Instance { get; private set; }

        public DeviceViewModel DefaultDevice { get; private set; }

        public ObservableCollection<DeviceViewModel> Devices { get; private set; }

        public Visibility ListVisibility => DefaultDevice.Apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility NoAppsPaneVisibility => DefaultDevice.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DeviceVisibility => _deviceService.VirtualDefaultDevice.IsDevicePresent ? Visibility.Visible : Visibility.Collapsed;

        public string NoItemsContent => !_deviceService.VirtualDefaultDevice.IsDevicePresent ? Properties.Resources.NoDevicesPanelContent : Properties.Resources.NoAppsPanelContent;

        public Visibility ExpandedPaneVisibility { get; private set; }

        public string ExpandText => ExpandedPaneVisibility == Visibility.Collapsed ? "\ue010" : "\ue011";

        public ViewState State { get; private set; }
        public bool IsDimmed { get; private set; }

        public static AppItemViewModel ExpandedApp { get; set; }
        public FrameworkElement ExpandedAppContainer { get; set; }
        public DependencyObject ExpandedAppContainerParent { get; set; }

        public event EventHandler<AppItemViewModel> AppExpanded = delegate { };
        public event EventHandler<object> AppCollapsed = delegate { };
        public event EventHandler<ViewState> StateChanged = delegate { };

        private readonly IAudioDeviceManager _deviceService;
        private readonly Timer _peakMeterTimer;

        public MainViewModel(IAudioDeviceManager deviceService)
        {
            State = ViewState.NotReady;

            if (Instance != null)
            {
                throw new InvalidOperationException("Only one MainViewModel may exist");
            }

            Instance = this;

            _deviceService = deviceService;
            _deviceService.VirtualDefaultDevice.PropertyChanged += VirtualDefaultDevice_PropertyChanged;
            _deviceService.DefaultPlaybackDeviceChanged += _deviceService_DefaultPlaybackDeviceChanged;
            _deviceService.Devices.CollectionChanged += Devices_CollectionChanged;

            Devices = new ObservableCollection<DeviceViewModel>();
            DefaultDevice = new DeviceViewModel(_deviceService, _deviceService.VirtualDefaultDevice);

            ExpandedPaneVisibility = Visibility.Collapsed;
            UpdateInterfaceState();
            Devices_CollectionChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

            _peakMeterTimer = new Timer(1000 / 30);
            _peakMeterTimer.AutoReset = true;
            _peakMeterTimer.Elapsed += PeakMeterTimer_Elapsed;
        }

        private void _deviceService_DefaultPlaybackDeviceChanged(object sender, IAudioDevice e)
        {
            foreach(var device in _deviceService.Devices)
            {
                CheckApplicability(device);
            }

            CheckApplicability(_deviceService.DefaultPlaybackDevice);
        }

        private void Devices_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    AddDevice((IAudioDevice)e.NewItems[0]);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    var removed = ((IAudioDevice)e.OldItems[0]).Id;

                    foreach(var device in Devices)
                    {
                        if (device.Device.Id == removed)
                        {
                            Devices.Remove(device);
                            break;
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Reset:

                    foreach (var device in _deviceService.Devices)
                    {
                        if (device != _deviceService.DefaultPlaybackDevice)
                        {
                            AddDevice(device);
                        }
                    }

                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        void AddDevice(IAudioDevice device)
        {
            CheckApplicability(device);
        }

        void CheckApplicability(IAudioDevice device)
        {
            var existing = Devices.FirstOrDefault(d => d.Device.Id == device.Id);

            if (_deviceService.DefaultPlaybackDevice == device)
            {
                Devices.Remove(existing);
            }
            else
            {
                if (!Devices.Any(d => d.Device.Id == device.Id))
                {
                    Devices.Add(new DeviceViewModel(_deviceService, device));
                    return;
                }
            }
        }

        private void PeakMeterTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            DefaultDevice.TriggerPeakCheck();

            foreach (var device in Devices)
            {
                device.TriggerPeakCheck();
            }
        }

        private void VirtualDefaultDevice_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_deviceService.VirtualDefaultDevice.IsDevicePresent))
            {
                UpdateInterfaceState();
            }
        }

        public void UpdateInterfaceState()
        {
            RaisePropertyChanged(nameof(ListVisibility));
            RaisePropertyChanged(nameof(NoAppsPaneVisibility));
            RaisePropertyChanged(nameof(NoItemsContent));
            RaisePropertyChanged(nameof(DeviceVisibility));
        }

        public void DoExpandCollapse()
        {
            ExpandedPaneVisibility = ExpandedPaneVisibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
            RaisePropertyChanged(nameof(ExpandedPaneVisibility));
            RaisePropertyChanged(nameof(ExpandText));
        }

        public void ChangeState(ViewState state)
        {
            var oldState = State;

            State = state;
            StateChanged(this, state);

            if (state == ViewState.Open)
            {
                _peakMeterTimer.Start();

                if (oldState == ViewState.Opening_CloseRequested)
                {
                    BeginClose();
                }
            }
            else if (state == ViewState.Hidden)
            {
                _peakMeterTimer.Stop();

                if (ExpandedApp != null)
                {
                    OnAppCollapsed();
                }
            }
        }

        public void OnAppExpanded(AppItemViewModel vm, ListViewItem lvi)
        {
            if (ExpandedApp != null)
            {
                OnAppCollapsed();
            }

            ExpandedAppContainer = lvi;
            ExpandedAppContainerParent = lvi.Parent;
            ExpandedApp = vm;

            AppExpanded?.Invoke(this, vm);

            IsDimmed = true;
            RaisePropertyChanged(nameof(IsDimmed));
        }

        public void OnAppCollapsed()
        {
            AppCollapsed?.Invoke(this, null);
            IsDimmed = false;
            RaisePropertyChanged(nameof(IsDimmed));
        }

        public void BeginOpen()
        {
            if (State == ViewState.Hidden)
            {
                ChangeState(ViewState.Opening);
            }

            // NotReady - Ignore, can't do anything.
            // Opening - Ignore. Already opening.
            // Opening_CloseRequested - Ignore, already opening and then closing.
            // Open - We're already open.
            // Closing - Drop event. Not worth the complexity?
        }

        public void BeginClose()
        {
            if (State == ViewState.Open)
            {
                ChangeState(ViewState.Closing);
            }
            else if (State == ViewState.Opening)
            {
                ChangeState(ViewState.Opening_CloseRequested);
            }

            // NotReady - Impossible.
            // Hidden - Nothing to do.
            // Opening_CloseRequested - Nothing to do.
            // Closing - Nothing to do.
        }
    }
}
