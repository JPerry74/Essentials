﻿using System;
using System.Collections.Generic;
using System.Linq;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.Devices.Codec;
using PepperDash.Essentials.Core.Devices.VideoCodec;
using PepperDash.Essentials.Core.PageManagers;
using PepperDash.Essentials.Core.Touchpanels.Keyboards;
using PepperDash.Essentials.UIDrivers;
using PepperDash.Essentials.UIDrivers.VC;

namespace PepperDash.Essentials
{
    /// <summary>
    /// 
    /// </summary>
    public class EssentialsHuddleVtc1PanelAvFunctionsDriver : PanelDriverBase, IAVWithVCDriver,IHasCallButton, IHasCalendarButton
    {
        #region UiDisplayMode enum

        public enum UiDisplayMode
        {
            Presentation,
            AudioSetup,
            Call,
            Start
        }

        #endregion

        /// <summary>
        /// Smart Object 15022
        /// </summary>
        private readonly SubpageReferenceList _activityFooterSrl;

        /// <summary>
        /// For hitting feedbacks
        /// </summary>
        private readonly BoolInputSig _callButtonSig;

        private readonly List<BoolInputSig> _currentDisplayModeSigsInUse = new List<BoolInputSig>();

        private readonly BoolInputSig _endMeetingButtonSig;

        /// <summary>
        /// The list of buttons on the header. Managed with visibility only
        /// </summary>
        //SmartObjectHeaderButtonList HeaderButtonsList;
        /// <summary>
        /// The AV page mangagers that have been used, to keep them alive for later
        /// </summary>
        private readonly Dictionary<object, PageManager> _pageManagers = new Dictionary<object, PageManager>();

        /// <summary>
        /// The parent driver for this
        /// </summary>
        private readonly PanelDriverBase _parent;

        private readonly BoolInputSig _shareButtonSig;

        //// Important smart objects

        /// <summary>
        /// Smart Object 3200
        /// </summary>
        private readonly SubpageReferenceList _sourceStagingSrl;

        private readonly CrestronTouchpanelPropertiesConfig _config;

        /// <summary>
        /// Interlocks the various call-related subpages
        /// </summary>
        private JoinedSigInterlock _callPagesInterlock;

        private BoolFeedback _callSharingInfoVisibleFeedback;

        /// <summary>
        /// All children attached to this driver.  For hiding and showing as a group.
        /// </summary>
        private List<PanelDriverBase> _childDrivers = new List<PanelDriverBase>();

        /// <summary>
        /// The mode showing. Presentation or call.
        /// </summary>
        private UiDisplayMode _currentMode = UiDisplayMode.Start;

        /// <summary>
        /// Current page manager running for a source
        /// </summary>
        private PageManager _currentSourcePageManager;

        /// <summary>
        /// Tracks the last meeting that was cancelled
        /// </summary>
        private string _lastMeetingDismissedId;

        private CTimer _nextMeetingTimer;

        /// <summary>
        /// 
        /// </summary>
        private ModalDialog _powerDownModal;

        /// <summary>
        /// Will auto-timeout a power off
        /// </summary>
        private CTimer _powerOffTimer;

        /// <summary>
        /// Controls timeout of notification ribbon timer
        /// </summary>
        private CTimer _ribbonTimer;

        /// <summary>
        /// Interlock for various source, camera, call control bars. The bar above the activity footer.  This is also 
        /// used to show start page
        /// </summary>
        private JoinedSigInterlock _stagingBarInterlock;

        /// <summary>
        /// The Video codec driver
        /// </summary>
        private EssentialsVideoCodecUiDriver _vcDriver;

        private EssentialsHuddleVtc1Room _currentRoom;

        private EssentialsHuddleTechPageDriver _techDriver;

        /// <summary>
        /// Constructor
        /// </summary>
        public EssentialsHuddleVtc1PanelAvFunctionsDriver(PanelDriverBase parent,
            CrestronTouchpanelPropertiesConfig config)
            : base(parent.TriList)
        {
            _config = config;
            _parent = parent;

            PopupInterlock = new JoinedSigInterlock(TriList);
            _stagingBarInterlock = new JoinedSigInterlock(TriList);
            _callPagesInterlock = new JoinedSigInterlock(TriList);

            _sourceStagingSrl = new SubpageReferenceList(TriList, UISmartObjectJoin.SourceStagingSRL, 3, 3, 3);

            _activityFooterSrl = new SubpageReferenceList(TriList, UISmartObjectJoin.ActivityFooterSRL, 3, 3, 3);
            _callButtonSig = _activityFooterSrl.BoolInputSig(2, 1);
            _shareButtonSig = _activityFooterSrl.BoolInputSig(1, 1);
            _endMeetingButtonSig = _activityFooterSrl.BoolInputSig(3, 1);

            MeetingOrContactMethodModalSrl = new SubpageReferenceList(TriList, UISmartObjectJoin.MeetingListSRL, 3, 3, 5);


            // buttons are added in SetCurrentRoom
            //HeaderButtonsList = new SmartObjectHeaderButtonList(TriList.SmartObjects[UISmartObjectJoin.HeaderButtonList]);

            SetupActivityFooterWhenRoomOff();

            ShowVolumeGauge = true;
            Keyboard = new HabaneroKeyboardController(TriList);
        }

        /// <summary>
        /// Whether volume ramping from this panel will show the volume
        /// gauge popup.
        /// </summary>
        public bool ShowVolumeGauge { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public uint PowerOffTimeout { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public string DefaultRoomKey { get; set; }

        /// <summary>
        /// The driver for the tech page. Lazy getter for memory usage
        /// </summary>
        private EssentialsHuddleTechPageDriver TechDriver
        {
            get
            {
                return _techDriver ?? (_techDriver = new EssentialsHuddleTechPageDriver(TriList,
                    _currentRoom.PropertiesConfig.Tech));
            }
        }

        #region IAVWithVCDriver Members

        /// <summary>
        /// 
        /// </summary>
        public EssentialsHuddleVtc1Room CurrentRoom
        {
            get { return _currentRoom; }
            set { SetCurrentRoom(value); }
        }

        /// <summary>
        /// 
        /// </summary>
        public SubpageReferenceList MeetingOrContactMethodModalSrl { get; set; }

        /// <summary>
        /// 
        /// </summary>
        //ModalDialog WarmingCoolingModal;
        /// <summary>
        /// Represents
        /// </summary>
        public JoinedSigInterlock PopupInterlock { get; private set; }

        /// <summary>
        /// The keyboard
        /// </summary>
        public HabaneroKeyboardController Keyboard { get; private set;
        }

        /// <summary>
        /// Reveals a message on the notification ribbon until cleared
        /// </summary>
        /// <param name="message">Text to display</param>
        /// <param name="timeout">Time in ms to display. 0 to keep on screen</param>
        public void ShowNotificationRibbon(string message, int timeout)
        {
            TriList.SetString(UIStringJoin.NotificationRibbonText, message);
            TriList.SetBool(UIBoolJoin.NotificationRibbonVisible, true);
            if (timeout > 0)
            {
                if (_ribbonTimer != null)
                {
                    _ribbonTimer.Stop();
                }
                _ribbonTimer = new CTimer(o =>
                {
                    TriList.SetBool(UIBoolJoin.NotificationRibbonVisible, false);
                    _ribbonTimer = null;
                }, timeout);
            }
        }

        /// <summary>
        /// Hides the notification ribbon
        /// </summary>
        public void HideNotificationRibbon()
        {
            TriList.SetBool(UIBoolJoin.NotificationRibbonVisible, false);
            if (_ribbonTimer != null)
            {
                _ribbonTimer.Stop();
                _ribbonTimer = null;
            }
        }

        /// <summary>
        /// Reveals the tech page and puts away anything that's in the way.
        /// </summary>
        public void ShowTech()
        {
            PopupInterlock.HideAndClear();
            TechDriver.Show();
        }

        /// <summary>
        /// 
        /// </summary>
        public void ActivityCallButtonPressed()
        {
            if (_vcDriver.IsVisible)
            {
                return;
            }
            HideLogo();
            HideNextMeetingPopup();
            TriList.SetBool(UIBoolJoin.StartPageVisible, false);
            TriList.SetBool(UIBoolJoin.SourceStagingBarVisible, false);
            TriList.SetBool(UIBoolJoin.SelectASourceVisible, false);
            if (_currentSourcePageManager != null)
            {
                _currentSourcePageManager.Hide();
            }
            PowerOnFromCall();
            _currentMode = UiDisplayMode.Call;
            SetActivityFooterFeedbacks();
            _vcDriver.Show();
        }

        /// <summary>
        /// Puts away modals and things that might be up when call comes in
        /// </summary>
        public void PrepareForCodecIncomingCall()
        {
            if (_powerDownModal != null && _powerDownModal.ModalIsVisible)
            {
                _powerDownModal.CancelDialog();
            }
            PopupInterlock.Hide();
        }

        #endregion

        /// <summary>
        /// Add a video codec driver to this
        /// </summary>
        /// <param name="vcd"></param>
        public void SetVideoCodecDriver(EssentialsVideoCodecUiDriver vcd)
        {
            _vcDriver = vcd;
        }

        /// <summary>
        /// 
        /// </summary>
        public override void Show()
        {
            if (CurrentRoom == null)
            {
                Debug.Console(1, "ERROR: AVUIFunctionsDriver, Cannot show. No room assigned");
                return;
            }

            switch (_config.HeaderStyle.ToLower())
            {
                case CrestronTouchpanelPropertiesConfig.Habanero:
                    TriList.SetSigFalseAction(UIBoolJoin.HeaderRoomButtonPress, () =>
                        PopupInterlock.ShowInterlockedWithToggle(UIBoolJoin.RoomHeaderPageVisible));
                    break;
                case CrestronTouchpanelPropertiesConfig.Verbose:
                    break;
            }

            TriList.SetBool(UIBoolJoin.DateAndTimeVisible, _config.ShowDate && _config.ShowTime);
            TriList.SetBool(UIBoolJoin.DateOnlyVisible, _config.ShowDate && !_config.ShowTime);
            TriList.SetBool(UIBoolJoin.TimeOnlyVisible, !_config.ShowDate && _config.ShowTime);

            TriList.SetBool(UIBoolJoin.TopBarHabaneroDynamicVisible, true);

            TriList.SetBool(UIBoolJoin.ActivityFooterVisible, true);

            // Privacy mute button
            TriList.SetSigFalseAction(UIBoolJoin.Volume1SpeechMutePressAndFB, _currentRoom.PrivacyModeToggle);
            _currentRoom.PrivacyModeIsOnFeedback.LinkInputSig(
                TriList.BooleanInput[UIBoolJoin.Volume1SpeechMutePressAndFB]);

            // Default to showing rooms/sources now.
            if (CurrentRoom.OnFeedback.BoolValue)
            {
                TriList.SetBool(UIBoolJoin.TapToBeginVisible, false);
                SetupActivityFooterWhenRoomOn();
            }
            else
            {
                TriList.SetBool(UIBoolJoin.StartPageVisible, true);
                TriList.SetBool(UIBoolJoin.TapToBeginVisible, true);
                SetupActivityFooterWhenRoomOff();
            }
            ShowCurrentDisplayModeSigsInUse();

            // *** Header Buttons ***

            // Generic "close" button for popup modals
            TriList.SetSigFalseAction(UIBoolJoin.InterlockedModalClosePress, PopupInterlock.HideAndClear);

            // Volume related things
            TriList.SetSigFalseAction(UIBoolJoin.VolumeDefaultPress, () => CurrentRoom.SetDefaultLevels());
            TriList.SetString(UIStringJoin.AdvancedVolumeSlider1Text, "Room");

            //if (TriList is CrestronApp)
            //    TriList.BooleanInput[UIBoolJoin.GearButtonVisible].BoolValue = false;
            //else
            //    TriList.BooleanInput[UIBoolJoin.GearButtonVisible].BoolValue = true;

            // power-related functions
            // Note: some of these are not directly-related to the huddle space UI, but are held over
            // in case
            TriList.SetSigFalseAction(UIBoolJoin.ShowPowerOffPress, EndMeetingPress);

            TriList.SetSigFalseAction(UIBoolJoin.DisplayPowerTogglePress, () =>
            {
                if (CurrentRoom != null && CurrentRoom.DefaultDisplay is IPower)
                {
                    (CurrentRoom.DefaultDisplay as IPower).PowerToggle();
                }
            });

            SetupNextMeetingTimer();

            base.Show();
        }

        /// <summary>
        /// Allows PopupInterlock to be toggled if the calls list is already visible, or if the codec is in a call
        /// </summary>
        public void ShowActiveCallsList()
        {
            TriList.SetBool(UIBoolJoin.CallEndAllConfirmVisible, true);
            if (PopupInterlock.CurrentJoin == UIBoolJoin.HeaderActiveCallsListVisible)
            {
                PopupInterlock.ShowInterlockedWithToggle(UIBoolJoin.HeaderActiveCallsListVisible);
            }
            else
            {
                var videoCodecBase = _currentRoom.ScheduleSource as VideoCodecBase;
                if (videoCodecBase != null && videoCodecBase.IsInCall)
                {
                    PopupInterlock.ShowInterlockedWithToggle(UIBoolJoin.HeaderActiveCallsListVisible);
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void ShowLogo()
        {
            if (CurrentRoom.LogoUrl == null)
            {
                TriList.SetBool(UIBoolJoin.LogoDefaultVisible, true);
                TriList.SetBool(UIBoolJoin.LogoUrlVisible, false);
            }
            else
            {
                TriList.SetBool(UIBoolJoin.LogoDefaultVisible, false);
                TriList.SetBool(UIBoolJoin.LogoUrlVisible, true);
                TriList.SetString(UIStringJoin.LogoUrl, _currentRoom.LogoUrl);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void HideLogo()
        {
            TriList.SetBool(UIBoolJoin.LogoDefaultVisible, false);
            TriList.SetBool(UIBoolJoin.LogoUrlVisible, false);
        }

        /// <summary>
        /// 
        /// </summary>
        public override void Hide()
        {
            HideAndClearCurrentDisplayModeSigsInUse();
            TriList.SetBool(UIBoolJoin.TopBarHabaneroDynamicVisible, false);
            TriList.BooleanInput[UIBoolJoin.ActivityFooterVisible].BoolValue = false;
            TriList.BooleanInput[UIBoolJoin.StartPageVisible].BoolValue = false;
            TriList.BooleanInput[UIBoolJoin.TapToBeginVisible].BoolValue = false;
            TriList.BooleanInput[UIBoolJoin.SelectASourceVisible].BoolValue = false;
            if (_nextMeetingTimer != null)
            {
                _nextMeetingTimer.Stop();
            }
            HideNextMeetingPopup();
            base.Hide();
        }

        private void SetupNextMeetingTimer()
        {
            var ss = _currentRoom.ScheduleSource;
            if (ss != null)
            {
                _nextMeetingTimer = new CTimer(o => ShowNextMeetingTimerCallback(), null, 0, 60000);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void ShowNextMeetingTimerCallback()
        {
            // Every 60 seconds, refresh the calendar
            RefreshMeetingsList();
            // check meetings list for the closest, joinable meeting
            var ss = _currentRoom.ScheduleSource;
            var meetings = ss.CodecSchedule.Meetings;

            if (meetings.Count > 0)
            {
                // If the room is off pester the user
                // If the room is on, and the meeting is joinable
                // and the LastMeetingDismissed != this meeting

                var lastMeetingDismissed = meetings.FirstOrDefault(m => m.Id == _lastMeetingDismissedId);
                Debug.Console(0, "*#* Room on: {0}, lastMeetingDismissedId: {1} {2} *#*",
                    CurrentRoom.OnFeedback.BoolValue,
                    _lastMeetingDismissedId,
                    lastMeetingDismissed != null ? lastMeetingDismissed.StartTime.ToShortTimeString() : "");

                var meeting = meetings.LastOrDefault(m => m.Joinable);
                if (CurrentRoom.OnFeedback.BoolValue
                    && lastMeetingDismissed == meeting)
                {
                    return;
                }

                _lastMeetingDismissedId = null;
                // Clear the popup when we run out of meetings
                if (meeting == null)
                {
                    HideNextMeetingPopup();
                }
                else
                {
                    TriList.SetString(UIStringJoin.MeetingsOrContactMethodListTitleText, "Upcoming meeting");
                    TriList.SetString(UIStringJoin.NextMeetingStartTimeText, meeting.StartTime.ToShortTimeString());
                    TriList.SetString(UIStringJoin.NextMeetingEndTimeText, meeting.EndTime.ToShortTimeString());
                    TriList.SetString(UIStringJoin.NextMeetingTitleText, meeting.Title);
                    TriList.SetString(UIStringJoin.NextMeetingNameText, meeting.Organizer);
                    TriList.SetString(UIStringJoin.NextMeetingButtonLabel, "Join");
                    TriList.SetSigFalseAction(UIBoolJoin.NextMeetingJoinPress, () =>
                    {
                        HideNextMeetingPopup();
                        PopupInterlock.Hide();
                        RoomOnAndDialMeeting(meeting);
                    });
                    TriList.SetString(UIStringJoin.NextMeetingSecondaryButtonLabel, "Show Schedule");
                    TriList.SetSigFalseAction(UIBoolJoin.CalendarHeaderButtonPress, () =>
                    {
                        HideNextMeetingPopup();
                        //CalendarPress();
                        RefreshMeetingsList();
                        PopupInterlock.ShowInterlocked(UIBoolJoin.MeetingsOrContacMethodsListVisible);
                    });
                    var indexOfNext = meetings.IndexOf(meeting) + 1;

                    // indexOf = 3, 4 meetings :  
                    TriList.SetString(UIStringJoin.NextMeetingFollowingMeetingText,
                        indexOfNext < meetings.Count
                            ? meetings[indexOfNext].StartTime.ToShortTimeString()
                            : "No more meetings today");

                    TriList.SetSigFalseAction(UIBoolJoin.NextMeetingModalClosePress, () =>
                    {
                        // Mark the meeting to not re-harass the user
                        if (CurrentRoom.OnFeedback.BoolValue)
                        {
                            _lastMeetingDismissedId = meeting.Id;
                        }
                        HideNextMeetingPopup();
                    });

                    TriList.SetBool(UIBoolJoin.NextMeetingModalVisible, true);
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void HideNextMeetingPopup()
        {
            TriList.SetBool(UIBoolJoin.NextMeetingModalVisible, false);
        }

        /// <summary>
        /// Calendar should only be visible when it's supposed to
        /// </summary>
        public void CalendarPress()
        {
            //RefreshMeetingsList(); // List should be up-to-date
            PopupInterlock.ShowInterlockedWithToggle(UIBoolJoin.MeetingsOrContacMethodsListVisible);
        }

        /// <summary>
        /// Dials a meeting after turning on room (if necessary)
        /// </summary>
        private void RoomOnAndDialMeeting(Meeting meeting)
        {
            Action dialAction = () =>
            {
                var d = _currentRoom.ScheduleSource as VideoCodecBase;
                if (d != null)
                {
                    d.Dial(meeting);
                    _lastMeetingDismissedId = meeting.Id; // To prevent prompts for already-joined call
                }
            };
            if (CurrentRoom.OnFeedback.BoolValue)
            {
                dialAction();
            }
            else
            {
                // Rig a one-time handler to catch when the room is warmed and then dial call
                EventHandler<FeedbackEventArgs> oneTimeHandler = null;
                oneTimeHandler = (o, a) =>
                {
                    if (!CurrentRoom.IsWarmingUpFeedback.BoolValue)
                    {
                        CurrentRoom.IsWarmingUpFeedback.OutputChange -= oneTimeHandler;
                        dialAction();
                    }
                };
                CurrentRoom.IsWarmingUpFeedback.OutputChange += oneTimeHandler;
                ActivityCallButtonPressed();
            }
        }

        /// <summary>
        /// When the room is off, set the footer SRL
        /// </summary>
        private void SetupActivityFooterWhenRoomOff()
        {
            _activityFooterSrl.Clear();
            _activityFooterSrl.AddItem(new SubpageReferenceListActivityItem(1, _activityFooterSrl, 0,
                b =>
                {
                    if (!b)
                    {
                        ActivityShareButtonPressed();
                    }
                }));
            _activityFooterSrl.AddItem(new SubpageReferenceListActivityItem(2, _activityFooterSrl, 3,
                b =>
                {
                    if (!b)
                    {
                        ActivityCallButtonPressed();
                    }
                }));
            _activityFooterSrl.Count = 2;
            TriList.SetUshort(UIUshortJoin.PresentationStagingCaretMode, 1); // right one slot
            TriList.SetUshort(UIUshortJoin.CallStagingCaretMode, 5); // left one slot
        }

        /// <summary>
        /// Sets up the footer SRL for when the room is on
        /// </summary>
        private void SetupActivityFooterWhenRoomOn()
        {
            _activityFooterSrl.Clear();
            _activityFooterSrl.AddItem(new SubpageReferenceListActivityItem(1, _activityFooterSrl, 0,
                b =>
                {
                    if (!b)
                    {
                        ActivityShareButtonPressed();
                    }
                }));
            _activityFooterSrl.AddItem(new SubpageReferenceListActivityItem(2, _activityFooterSrl, 3,
                b =>
                {
                    if (!b)
                    {
                        ActivityCallButtonPressed();
                    }
                }));
            _activityFooterSrl.AddItem(new SubpageReferenceListActivityItem(3, _activityFooterSrl, 4,
                b =>
                {
                    if (!b)
                    {
                        EndMeetingPress();
                    }
                }));
            _activityFooterSrl.Count = 3;
            TriList.SetUshort(UIUshortJoin.PresentationStagingCaretMode, 2); // center
            TriList.SetUshort(UIUshortJoin.CallStagingCaretMode, 0); // left -2
        }

        /// <summary>
        /// Single point call for setting the feedbacks on the activity buttons
        /// </summary>
        private void SetActivityFooterFeedbacks()
        {
            _callButtonSig.BoolValue = _currentMode == UiDisplayMode.Call
                                      && CurrentRoom.ShutdownType == eShutdownType.None;
            _shareButtonSig.BoolValue = _currentMode == UiDisplayMode.Presentation
                                       && CurrentRoom.ShutdownType == eShutdownType.None;
            _endMeetingButtonSig.BoolValue = CurrentRoom.ShutdownType != eShutdownType.None;
        }

        /// <summary>
        /// Attached to activity list share button
        /// </summary>
        private void ActivityShareButtonPressed()
        {
            SetupSourceList();
            if (_vcDriver.IsVisible)
            {
                _vcDriver.Hide();
            }
            HideNextMeetingPopup();
            TriList.SetBool(UIBoolJoin.StartPageVisible, false);
            TriList.SetBool(UIBoolJoin.CallStagingBarVisible, false);
            TriList.SetBool(UIBoolJoin.SourceStagingBarVisible, true);
            // Run default source when room is off and share is pressed
            if (!CurrentRoom.OnFeedback.BoolValue)
            {
                if (!CurrentRoom.OnFeedback.BoolValue)
                {
                    // If there's no default, show UI elements
                    if (!CurrentRoom.RunDefaultPresentRoute())
                    {
                        TriList.SetBool(UIBoolJoin.SelectASourceVisible, true);
                    }
                }
            }
            else // room is on show what's active or select a source if nothing is yet active
            {
                if (CurrentRoom.CurrentSourceInfo == null ||
                    CurrentRoom.CurrentSourceInfoKey == EssentialsHuddleVtc1Room.DefaultCodecRouteString)
                {
                    TriList.SetBool(UIBoolJoin.SelectASourceVisible, true);
                }
                else if (_currentSourcePageManager != null)
                {
                    _currentSourcePageManager.Show();
                }
            }
            _currentMode = UiDisplayMode.Presentation;
            SetupSourceList();
            SetActivityFooterFeedbacks();
        }

        /// <summary>
        /// Powers up the system to the codec route, if not already on.
        /// </summary>
        private void PowerOnFromCall()
        {
            if (!CurrentRoom.OnFeedback.BoolValue)
            {
                _currentRoom.RunDefaultCallRoute();
            }
        }

        /// <summary>
        /// Shows all sigs that are in CurrentDisplayModeSigsInUse
        /// </summary>
        private void ShowCurrentDisplayModeSigsInUse()
        {
            foreach (var sig in _currentDisplayModeSigsInUse)
            {
                sig.BoolValue = true;
            }
        }

        /// <summary>
        /// Hides all CurrentDisplayModeSigsInUse sigs and clears the array
        /// </summary>
        private void HideAndClearCurrentDisplayModeSigsInUse()
        {
            foreach (var sig in _currentDisplayModeSigsInUse)
            {
                sig.BoolValue = false;
            }
            _currentDisplayModeSigsInUse.Clear();
        }


        /// <summary>
        /// Loads the appropriate Sigs into CurrentDisplayModeSigsInUse and shows them
        /// </summary>
        private void ShowCurrentSource()
        {
            if (CurrentRoom.CurrentSourceInfo == null)
            {
                return;
            }

            if (CurrentRoom.CurrentSourceInfo.SourceDevice == null)
            {
                TriList.SetBool(UIBoolJoin.SelectASourceVisible, true);
                return;
            }

            var uiDev = CurrentRoom.CurrentSourceInfo.SourceDevice as IUiDisplayInfo;
            // If we need a page manager, get an appropriate one
            if (uiDev == null)
            {
                return;
            }

            TriList.SetBool(UIBoolJoin.SelectASourceVisible, false);
            // Got an existing page manager, get it
            PageManager pm;
            if (_pageManagers.ContainsKey(uiDev))
            {
                pm = _pageManagers[uiDev];
            }
                // Otherwise make an apporiate one
            else if (uiDev is ISetTopBoxControls)
            {
                pm = new SetTopBoxThreePanelPageManager(uiDev as ISetTopBoxControls, TriList);
            }
            else if (uiDev is IDiscPlayerControls)
            {
                pm = new DiscPlayerMediumPageManager(uiDev as IDiscPlayerControls, TriList);
            }
            else
            {
                pm = new DefaultPageManager(uiDev, TriList);
            }
            _pageManagers[uiDev] = pm;
            _currentSourcePageManager = pm;
            pm.Show();
        }

        /// <summary>
        /// Called from button presses on source, where We can assume we want
        /// to change to the proper screen.
        /// </summary>
        /// <param name="key">The key name of the route to run</param>
        private void UiSelectSource(string key)
        {
            // Run the route and when it calls back, show the source
            CurrentRoom.RunRouteAction(key, () => { });
        }

        /// <summary>
        /// 
        /// </summary>
        public void EndMeetingPress()
        {
            if (!CurrentRoom.OnFeedback.BoolValue
                || CurrentRoom.ShutdownPromptTimer.IsRunningFeedback.BoolValue)
            {
                return;
            }

            CurrentRoom.StartShutdown(eShutdownType.Manual);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ShutdownPromptTimer_HasStarted(object sender, EventArgs e)
        {
            // Do we need to check where the UI is? No?
            var timer = CurrentRoom.ShutdownPromptTimer;
            SetActivityFooterFeedbacks();

            if (CurrentRoom.ShutdownType == eShutdownType.Manual || CurrentRoom.ShutdownType == eShutdownType.Vacancy)
            {
                _powerDownModal = new ModalDialog(TriList);
                var message = string.Format("Meeting will end in {0} seconds", CurrentRoom.ShutdownPromptSeconds);

                // Attach timer things to modal
                CurrentRoom.ShutdownPromptTimer.TimeRemainingFeedback.OutputChange +=
                    ShutdownPromptTimer_TimeRemainingFeedback_OutputChange;
                CurrentRoom.ShutdownPromptTimer.PercentFeedback.OutputChange +=
                    ShutdownPromptTimer_PercentFeedback_OutputChange;

                // respond to offs by cancelling dialog
                var onFb = CurrentRoom.OnFeedback;
                EventHandler<FeedbackEventArgs> offHandler = null;
                offHandler = (o, a) =>
                {
                    if (!onFb.BoolValue)
                    {
                        _powerDownModal.HideDialog();
                        SetActivityFooterFeedbacks();
                        onFb.OutputChange -= offHandler;
                    }
                };
                onFb.OutputChange += offHandler;

                _powerDownModal.PresentModalDialog(2, "End Meeting", "Power", message, "Cancel", "End Meeting Now", true,
                    true,
                    but =>
                    {
                        if (but != 2) // any button except for End cancels
                        {
                            timer.Cancel();
                        }
                        else
                        {
                            timer.Finish();
                        }
                    });
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ShutdownPromptTimer_HasFinished(object sender, EventArgs e)
        {
            SetActivityFooterFeedbacks();
            CurrentRoom.ShutdownPromptTimer.TimeRemainingFeedback.OutputChange -=
                ShutdownPromptTimer_TimeRemainingFeedback_OutputChange;
            CurrentRoom.ShutdownPromptTimer.PercentFeedback.OutputChange -=
                ShutdownPromptTimer_PercentFeedback_OutputChange;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ShutdownPromptTimer_WasCancelled(object sender, EventArgs e)
        {
            if (_powerDownModal != null)
            {
                _powerDownModal.HideDialog();
            }
            SetActivityFooterFeedbacks();

            CurrentRoom.ShutdownPromptTimer.TimeRemainingFeedback.OutputChange +=
                ShutdownPromptTimer_TimeRemainingFeedback_OutputChange;
            CurrentRoom.ShutdownPromptTimer.PercentFeedback.OutputChange -=
                ShutdownPromptTimer_PercentFeedback_OutputChange;
        }

        /// <summary>
        /// Event handler for countdown timer on power off modal
        /// </summary>
        private void ShutdownPromptTimer_TimeRemainingFeedback_OutputChange(object sender, EventArgs e)
        {
            var stringFeedback = sender as StringFeedback;
            if (stringFeedback == null)
            {
                return;
            }
            var message = string.Format("Meeting will end in {0} seconds", stringFeedback.StringValue);
            TriList.StringInput[ModalDialog.MessageTextJoin].StringValue = message;
        }

        /// <summary>
        /// Event handler for percentage on power off countdown
        /// </summary>
        private void ShutdownPromptTimer_PercentFeedback_OutputChange(object sender, EventArgs e)
        {
            var intFeedback = sender as IntFeedback;
            if (intFeedback == null)
            {
                return;
            }
            var value = (ushort) (intFeedback.UShortValue*65535/100);
            TriList.UShortInput[ModalDialog.TimerGaugeJoin].UShortValue = value;
        }

        /// <summary>
        /// 
        /// </summary>
        private void CancelPowerOffTimer()
        {
            if (_powerOffTimer != null)
            {
                _powerOffTimer.Stop();
                _powerOffTimer = null;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="state"></param>
        public void VolumeUpPress(bool state)
        {
            if (CurrentRoom.CurrentVolumeControls != null)
            {
                CurrentRoom.CurrentVolumeControls.VolumeUp(state);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="state"></param>
        public void VolumeDownPress(bool state)
        {
            if (CurrentRoom.CurrentVolumeControls != null)
            {
                CurrentRoom.CurrentVolumeControls.VolumeDown(state);
            }
        }

        /// <summary>
        /// Helper for property setter. Sets the panel to the given room, latching up all functionality
        /// </summary>
        private void RefreshCurrentRoom(EssentialsHuddleVtc1Room room)
        {
            if (_currentRoom != null)
            {
                // Disconnect current room
                _currentRoom.CurrentVolumeDeviceChange -= CurrentRoom_CurrentAudioDeviceChange;
                ClearAudioDeviceConnections();
                _currentRoom.CurrentSourceChange -= CurrentRoom_SourceInfoChange;
                DisconnectSource(_currentRoom.CurrentSourceInfo);
                _currentRoom.ShutdownPromptTimer.HasStarted -= ShutdownPromptTimer_HasStarted;
                _currentRoom.ShutdownPromptTimer.HasFinished -= ShutdownPromptTimer_HasFinished;
                _currentRoom.ShutdownPromptTimer.WasCancelled -= ShutdownPromptTimer_WasCancelled;

                _currentRoom.OnFeedback.OutputChange -= CurrentRoom_OnFeedback_OutputChange;
                _currentRoom.IsWarmingUpFeedback.OutputChange -= CurrentRoom_IsWarmingFeedback_OutputChange;
                _currentRoom.IsCoolingDownFeedback.OutputChange -= CurrentRoom_IsCoolingDownFeedback_OutputChange;
                _currentRoom.InCallFeedback.OutputChange -= CurrentRoom_InCallFeedback_OutputChange;
            }

            _currentRoom = room;

            if (_currentRoom != null)
            {
                // get the source list config and set up the source list

                SetupSourceList();

                // Name and logo
                TriList.StringInput[UIStringJoin.CurrentRoomName].StringValue = _currentRoom.Name;
                ShowLogo();

                // Shutdown timer
                _currentRoom.ShutdownPromptTimer.HasStarted += ShutdownPromptTimer_HasStarted;
                _currentRoom.ShutdownPromptTimer.HasFinished += ShutdownPromptTimer_HasFinished;
                _currentRoom.ShutdownPromptTimer.WasCancelled += ShutdownPromptTimer_WasCancelled;

                // Link up all the change events from the room
                _currentRoom.OnFeedback.OutputChange += CurrentRoom_OnFeedback_OutputChange;
                CurrentRoom_SyncOnFeedback();
                _currentRoom.IsWarmingUpFeedback.OutputChange += CurrentRoom_IsWarmingFeedback_OutputChange;
                _currentRoom.IsCoolingDownFeedback.OutputChange += CurrentRoom_IsCoolingDownFeedback_OutputChange;
                _currentRoom.InCallFeedback.OutputChange += CurrentRoom_InCallFeedback_OutputChange;


                _currentRoom.CurrentVolumeDeviceChange += CurrentRoom_CurrentAudioDeviceChange;
                RefreshAudioDeviceConnections();
                _currentRoom.CurrentSourceChange += CurrentRoom_SourceInfoChange;
                RefreshSourceInfo();

                if (_currentRoom.VideoCodec is IHasScheduleAwareness)
                {
                    (_currentRoom.VideoCodec as IHasScheduleAwareness).CodecSchedule.MeetingsListHasChanged +=
                        CodecSchedule_MeetingsListHasChanged;
                }

                _callSharingInfoVisibleFeedback =
                    new BoolFeedback(() => _currentRoom.VideoCodec.SharingContentIsOnFeedback.BoolValue);
                _currentRoom.VideoCodec.SharingContentIsOnFeedback.OutputChange +=
                    SharingContentIsOnFeedback_OutputChange;
                _callSharingInfoVisibleFeedback.LinkInputSig(TriList.BooleanInput[UIBoolJoin.CallSharedSourceInfoVisible]);

                SetActiveCallListSharingContentStatus();

                if (_currentRoom != null)
                {
                    _currentRoom.CurrentSourceChange +=
                        CurrentRoom_CurrentSingleSourceChange;
                }

                TriList.SetSigFalseAction(UIBoolJoin.CallStopSharingPress,
                    () => _currentRoom.RunRouteAction("codecOsd", _currentRoom.SourceListKey));

                var essentialsPanelMainInterfaceDriver = _parent as EssentialsPanelMainInterfaceDriver;
                if (essentialsPanelMainInterfaceDriver != null)
                {
                    essentialsPanelMainInterfaceDriver.HeaderDriver.SetupHeaderButtons(this, _currentRoom);
                }
            }
            else
            {
                // Clear sigs that need to be
                TriList.StringInput[UIStringJoin.CurrentRoomName].StringValue = "Select a room";
            }
        }

        private void SetCurrentRoom(EssentialsHuddleVtc1Room room)
        {
            if (_currentRoom == room || room == null)
            {
                return;
            }
            // Disconnect current (probably never called)

            room.ConfigChanged -= room_ConfigChanged;
            room.ConfigChanged += room_ConfigChanged;

            RefreshCurrentRoom(room);
        }

        /// <summary>
        /// Fires when room config of current room has changed.  Meant to refresh room values to propegate any updates to UI
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void room_ConfigChanged(object sender, EventArgs e)
        {
            RefreshCurrentRoom(_currentRoom);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CurrentRoom_InCallFeedback_OutputChange(object sender, EventArgs e)
        {
            var inCall = _currentRoom.InCallFeedback.BoolValue;
            if (inCall)
            {
                // Check if transitioning to in call - and non-sharable source is in use
                if (CurrentRoom.CurrentSourceInfo != null && CurrentRoom.CurrentSourceInfo.DisableCodecSharing)
                {
                    Debug.Console(1, CurrentRoom, "Transitioning to in-call, cancelling non-sharable source");
                    CurrentRoom.RunRouteAction("codecOsd", CurrentRoom.SourceListKey);
                }
            }

            SetupSourceList();
        }

        /// <summary>
        /// 
        /// </summary>
        private void SetupSourceList()
        {
            var inCall = _currentRoom.InCallFeedback.BoolValue;
            var config = ConfigReader.ConfigObject.SourceLists;
            if (config.ContainsKey(_currentRoom.SourceListKey))
            {
                var srcList = config[_currentRoom.SourceListKey].OrderBy(kv => kv.Value.Order);

                // Setup sources list			
                _sourceStagingSrl.Clear();
                uint i = 1; // counter for UI list
                foreach (var kvp in srcList)
                {
                    var srcConfig = kvp.Value;
                    Debug.Console(1, "**** {0}, {1}, {2}, {3}, {4}", srcConfig.PreferredName,
                        srcConfig.IncludeInSourceList,
                        srcConfig.DisableCodecSharing, inCall, _currentMode);
                    // Skip sources marked as not included, and filter list of non-sharable sources when in call
                    // or on share screen
                    if (!srcConfig.IncludeInSourceList || (inCall && srcConfig.DisableCodecSharing)
                        || _currentMode == UiDisplayMode.Call && srcConfig.DisableCodecSharing)
                    {
                        Debug.Console(1, "Skipping {0}", srcConfig.PreferredName);
                        continue;
                    }

                    var routeKey = kvp.Key;
                    var item = new SubpageReferenceListSourceItem(i++, _sourceStagingSrl, srcConfig,
                        b =>
                        {
                            if (!b)
                            {
                                UiSelectSource(routeKey);
                            }
                        });
                    _sourceStagingSrl.AddItem(item); // add to the SRL
                    item.RegisterForSourceChange(_currentRoom);
                }
                _sourceStagingSrl.Count = (ushort) (i - 1);
            }
        }

        /// <summary>
        /// If the schedule changes, this event will fire
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CodecSchedule_MeetingsListHasChanged(object sender, EventArgs e)
        {
            RefreshMeetingsList();
        }

        /// <summary>
        /// Updates the current shared source label on the call list when the source changes
        /// </summary>
        /// <param name="info"></param>
        /// <param name="type"></param>
        private void CurrentRoom_CurrentSingleSourceChange(SourceListItem info, ChangeType type)
        {
            if (_currentRoom.VideoCodec.SharingContentIsOnFeedback.BoolValue && _currentRoom.CurrentSourceInfo != null)
            {
                TriList.StringInput[UIStringJoin.CallSharedSourceNameText].StringValue =
                    _currentRoom.CurrentSourceInfo.PreferredName;
            }
        }

        /// <summary>
        /// Fires when the sharing source feedback of the codec changes
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SharingContentIsOnFeedback_OutputChange(object sender, EventArgs e)
        {
            SetActiveCallListSharingContentStatus();
        }

        /// <summary>
        /// Sets the values for the text and button visibilty for the active call list source sharing info
        /// </summary>
        private void SetActiveCallListSharingContentStatus()
        {
            _callSharingInfoVisibleFeedback.FireUpdate();

            string callListSharedSourceLabel;

            if (_currentRoom.VideoCodec.SharingContentIsOnFeedback.BoolValue && _currentRoom.CurrentSourceInfo != null)
            {
                Debug.Console(0, "*#* CurrentRoom.CurrentSourceInfo = {0}",
                    _currentRoom.CurrentSourceInfo != null ? _currentRoom.CurrentSourceInfo.SourceKey : "Nada!");
                callListSharedSourceLabel = _currentRoom.CurrentSourceInfo.PreferredName;
            }
            else
            {
                callListSharedSourceLabel = "None";
            }

            TriList.StringInput[UIStringJoin.CallSharedSourceNameText].StringValue = callListSharedSourceLabel;
        }

        /// <summary>
        /// 
        /// </summary>
        private void RefreshMeetingsList()
        {
            // See if this is helpful or if the callback response in the codec class maybe doesn't come it time?
            // Let's build list from event
            // CurrentRoom.ScheduleSource.GetSchedule();

            TriList.SetString(UIStringJoin.MeetingsOrContactMethodListIcon, "Calendar");
            TriList.SetString(UIStringJoin.MeetingsOrContactMethodListTitleText, "Today's Meetings");

            ushort i = 0;
            foreach (var m in _currentRoom.ScheduleSource.CodecSchedule.Meetings)
            {
                i++;
                MeetingOrContactMethodModalSrl.StringInputSig(i, 1).StringValue = m.StartTime.ToShortTimeString();
                MeetingOrContactMethodModalSrl.StringInputSig(i, 2).StringValue = m.EndTime.ToShortTimeString();
                MeetingOrContactMethodModalSrl.StringInputSig(i, 3).StringValue = m.Title;
                MeetingOrContactMethodModalSrl.StringInputSig(i, 4).StringValue = string.Format("<br>{0}", m.Organizer);
                MeetingOrContactMethodModalSrl.StringInputSig(i, 5).StringValue = "Join";
                MeetingOrContactMethodModalSrl.BoolInputSig(i, 2).BoolValue = m.Joinable;
                var mm = m; // lambda scope
                MeetingOrContactMethodModalSrl.GetBoolFeedbackSig(i, 1).SetSigFalseAction(() =>
                {
                    PopupInterlock.Hide();
                    ActivityCallButtonPressed();
                    var d = _currentRoom.ScheduleSource as VideoCodecBase;
                    if (d != null)
                    {
                        RoomOnAndDialMeeting(mm);
                    }
                });
            }
            MeetingOrContactMethodModalSrl.Count = i;

            if (i == 0) // Show item indicating no meetings are booked for rest of day
            {
                MeetingOrContactMethodModalSrl.Count = 1;

                MeetingOrContactMethodModalSrl.StringInputSig(1, 1).StringValue = string.Empty;
                MeetingOrContactMethodModalSrl.StringInputSig(1, 2).StringValue = string.Empty;
                MeetingOrContactMethodModalSrl.StringInputSig(1, 3).StringValue =
                    "No Meetings are booked for the remainder of the day.";
                MeetingOrContactMethodModalSrl.StringInputSig(1, 4).StringValue = string.Empty;
                MeetingOrContactMethodModalSrl.StringInputSig(1, 5).StringValue = string.Empty;
            }
        }

        /// <summary>
        /// For room on/off changes
        /// </summary>
        private void CurrentRoom_OnFeedback_OutputChange(object sender, EventArgs e)
        {
            CurrentRoom_SyncOnFeedback();
        }

        /// <summary>
        /// 
        /// </summary>
        private void CurrentRoom_SyncOnFeedback()
        {
            var value = _currentRoom.OnFeedback.BoolValue;
            TriList.BooleanInput[UIBoolJoin.RoomIsOn].BoolValue = value;

            TriList.BooleanInput[UIBoolJoin.StartPageVisible].BoolValue = !value;

            if (value) //ON
            {
                SetupActivityFooterWhenRoomOn();
                TriList.BooleanInput[UIBoolJoin.SelectASourceVisible].BoolValue = false;
                TriList.BooleanInput[UIBoolJoin.VolumeDualMute1Visible].BoolValue = true;
            }
            else
            {
                _currentMode = UiDisplayMode.Start;
                if (_vcDriver.IsVisible)
                {
                    _vcDriver.Hide();
                }
                SetupActivityFooterWhenRoomOff();
                ShowLogo();
                SetActivityFooterFeedbacks();
                TriList.BooleanInput[UIBoolJoin.VolumeDualMute1Visible].BoolValue = false;
                TriList.BooleanInput[UIBoolJoin.SourceStagingBarVisible].BoolValue = false;
                // Clear this so that the pesky meeting warning can resurface every minute when off
                _lastMeetingDismissedId = null;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void CurrentRoom_IsWarmingFeedback_OutputChange(object sender, EventArgs e)
        {
            if (CurrentRoom.IsWarmingUpFeedback.BoolValue)
            {
                ShowNotificationRibbon("Room is powering on. Please wait...", 0);
            }
            else
            {
                ShowNotificationRibbon("Room is powered on. Welcome.", 2000);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CurrentRoom_IsCoolingDownFeedback_OutputChange(object sender, EventArgs e)
        {
            if (CurrentRoom.IsCoolingDownFeedback.BoolValue)
            {
                ShowNotificationRibbon("Room is powering off. Please wait.", 0);
            }
            else
            {
                HideNotificationRibbon();
            }
        }

        /// <summary>
        /// Hides source for provided source info
        /// </summary>
        /// <param name="previousInfo"></param>
        private void DisconnectSource(SourceListItem previousInfo)
        {
            if (previousInfo == null)
            {
                return;
            }

            // Hide whatever is showing
            if (IsVisible)
            {
                if (_currentSourcePageManager != null)
                {
                    _currentSourcePageManager.Hide();
                    _currentSourcePageManager = null;
                }
            }

            var previousDev = previousInfo.SourceDevice;

            // device type interfaces
            if (previousDev is ISetTopBoxControls)
            {
                (previousDev as ISetTopBoxControls).UnlinkButtons(TriList);
            }
            // common interfaces
            if (previousDev is IChannel)
            {
                (previousDev as IChannel).UnlinkButtons(TriList);
            }
            if (previousDev is IColor)
            {
                (previousDev as IColor).UnlinkButtons(TriList);
            }
            if (previousDev is IDPad)
            {
                (previousDev as IDPad).UnlinkButtons(TriList);
            }
            if (previousDev is IDvr)
            {
                (previousDev as IDvr).UnlinkButtons(TriList);
            }
            if (previousDev is INumericKeypad)
            {
                (previousDev as INumericKeypad).UnlinkButtons(TriList);
            }
            if (previousDev is IPower)
            {
                (previousDev as IPower).UnlinkButtons(TriList);
            }
            if (previousDev is ITransport)
            {
                (previousDev as ITransport).UnlinkButtons(TriList);
            }
        }

        /// <summary>
        /// Refreshes and shows the room's current source
        /// </summary>
        private void RefreshSourceInfo()
        {
            var routeInfo = CurrentRoom.CurrentSourceInfo;
            // This will show off popup too
            if (IsVisible && !_vcDriver.IsVisible)
            {
                ShowCurrentSource();
            }

            if (routeInfo == null) // || !CurrentRoom.OnFeedback.BoolValue)
            {
                // Check for power off and insert "Room is off"
                TriList.StringInput[UIStringJoin.CurrentSourceName].StringValue = "Room is off";
                TriList.StringInput[UIStringJoin.CurrentSourceIcon].StringValue = "Power";
                Hide();
                _parent.Show();
                return;
            }
            TriList.StringInput[UIStringJoin.CurrentSourceName].StringValue = routeInfo.PreferredName;
            TriList.StringInput[UIStringJoin.CurrentSourceIcon].StringValue = routeInfo.Icon; // defaults to "blank"

            //code that was here was unreachable becuase if we get past the if statement, routeInfo is

            // Connect controls
            if (routeInfo.SourceDevice != null)
            {
                ConnectControlDeviceMethods(routeInfo.SourceDevice);
            }
        }

        /// <summary>
        /// Attach the source to the buttons and things
        /// </summary>
        private void ConnectControlDeviceMethods(Device dev)
        {
            if (dev is ISetTopBoxControls)
            {
                (dev as ISetTopBoxControls).LinkButtons(TriList);
            }
            if (dev is IChannel)
            {
                (dev as IChannel).LinkButtons(TriList);
            }
            if (dev is IColor)
            {
                (dev as IColor).LinkButtons(TriList);
            }
            if (dev is IDPad)
            {
                (dev as IDPad).LinkButtons(TriList);
            }
            if (dev is IDvr)
            {
                (dev as IDvr).LinkButtons(TriList);
            }
            if (dev is INumericKeypad)
            {
                (dev as INumericKeypad).LinkButtons(TriList);
            }
            if (dev is IPower)
            {
                (dev as IPower).LinkButtons(TriList);
            }
            if (dev is ITransport)
            {
                (dev as ITransport).LinkButtons(TriList);
            }
        }

        /// <summary>
        /// Detaches the buttons and feedback from the room's current audio device
        /// </summary>
        private void ClearAudioDeviceConnections()
        {
            TriList.ClearBoolSigAction(UIBoolJoin.VolumeUpPress);
            TriList.ClearBoolSigAction(UIBoolJoin.VolumeDownPress);
            TriList.ClearBoolSigAction(UIBoolJoin.Volume1ProgramMutePressAndFB);

            var fDev = CurrentRoom.CurrentVolumeControls as IBasicVolumeWithFeedback;
            if (fDev != null)
            {
                TriList.ClearUShortSigAction(UIUshortJoin.VolumeSlider1Value);
                fDev.VolumeLevelFeedback.UnlinkInputSig(
                    TriList.UShortInput[UIUshortJoin.VolumeSlider1Value]);
            }
        }

        /// <summary>
        /// Attaches the buttons and feedback to the room's current audio device
        /// </summary>
        private void RefreshAudioDeviceConnections()
        {
            var dev = CurrentRoom.CurrentVolumeControls;
            if (dev != null) // connect buttons
            {
                TriList.SetBoolSigAction(UIBoolJoin.VolumeUpPress, VolumeUpPress);
                TriList.SetBoolSigAction(UIBoolJoin.VolumeDownPress, VolumeDownPress);
                TriList.SetSigFalseAction(UIBoolJoin.Volume1ProgramMutePressAndFB, dev.MuteToggle);
            }

            var fbDev = dev as IBasicVolumeWithFeedback;
            if (fbDev == null) // this should catch both IBasicVolume and IBasicVolumeWithFeeback
            {
                TriList.UShortInput[UIUshortJoin.VolumeSlider1Value].UShortValue = 0;
            }
            else
            {
                // slider
                TriList.SetUShortSigAction(UIUshortJoin.VolumeSlider1Value, fbDev.SetVolume);
                // feedbacks
                fbDev.MuteFeedback.LinkInputSig(TriList.BooleanInput[UIBoolJoin.Volume1ProgramMutePressAndFB]);
                fbDev.VolumeLevelFeedback.LinkInputSig(
                    TriList.UShortInput[UIUshortJoin.VolumeSlider1Value]);
            }
        }

        /// <summary>
        /// Handler for when the room's volume control device changes
        /// </summary>
        private void CurrentRoom_CurrentAudioDeviceChange(object sender, VolumeDeviceChangeEventArgs args)
        {
            if (args.Type == ChangeType.WillChange)
            {
                ClearAudioDeviceConnections();
            }
            else // did change
            {
                RefreshAudioDeviceConnections();
            }
        }

        /// <summary>
        /// Handles source change
        /// </summary>
        private void CurrentRoom_SourceInfoChange(SourceListItem info, ChangeType change)
        {
            if (change == ChangeType.WillChange)
            {
                DisconnectSource(info);
            }
            else
            {
                RefreshSourceInfo();
            }
        }
    }

    /// <summary>
    /// For hanging off various common AV things that child drivers might need from a parent AV driver
    /// </summary>
    public interface IAVDriver
    {
        JoinedSigInterlock PopupInterlock { get; }
        void ShowNotificationRibbon(string message, int timeout);
        void HideNotificationRibbon();
        void ShowTech();
    }

    /// <summary>
    /// For hanging off various common VC things that child drivers might need from a parent AV driver
    /// </summary>
    public interface IAVWithVCDriver : IAVDriver
    {
        EssentialsHuddleVtc1Room CurrentRoom { get; }

        HabaneroKeyboardController Keyboard { get; }
        SubpageReferenceList MeetingOrContactMethodModalSrl { get; }

        /// <summary>
        /// Exposes the ability to switch into call mode
        /// </summary>
        void ActivityCallButtonPressed();

        /// <summary>
        /// Allows the codec to trigger the main UI to clear up if call is coming in.
        /// </summary>
        void PrepareForCodecIncomingCall();
    }
}