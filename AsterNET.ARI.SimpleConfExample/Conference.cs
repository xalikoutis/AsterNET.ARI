/*
 * MiniConf AsterNET.ARI Conference Sample
 * Copyright Ben Merrills (ben at mersontech co uk), all rights reserved.
 * https://asternetari.codeplex.com/
 * https://asternetari.codeplex.com/license
 * 
 * No Warranty. The Software is provided "as is" without warranty of any kind, either express or implied, 
 * including without limitation any implied warranties of condition, uninterrupted use, merchantability, 
 * fitness for a particular purpose, or non-infringement.
 *   
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AsterNET.ARI.Models;

namespace AsterNET.ARI.SimpleConfExample
{
    public enum ConferenceState
    {
        Destroyed = -1, // Conference no longer valid
        Destroying = 0, // conf not ready, destroying bridge
        Creating = 1, // conf not ready, creating bridge
        Ready = 2, // Conf bridge is ready to accept channels
        ReadyWaiting = 3, // Conf is ready but playing MOH until members join, or admin joins (todo)
        Muted = 4, // no one can speak (check if MOH should be played)
        AdminMuted = 5 // only the admins can speak
    }

    public class Conference
    {
        #region Private Properties

        private readonly StasisEndpoint _endPoint;
        private ConferenceState _state;
        private ARIClient _client;

        #endregion

        #region Public Properties

        public Bridge Confbridge;
        public string ConferenceName;

        public List<ConferenceUser> ConferenceUsers = new List<ConferenceUser>();
        public Guid Id;

        public ConferenceState State
        {
            get { return _state; }
            set
            {
                _state = value;
                Debug.Print("Conference {0} is now in state: {1}", ConferenceName, State);
            }
        }

        #endregion

        public Conference(StasisEndpoint e, ARIClient c, Guid id, string name)
        {
            _endPoint = e;
            _client = c;
            Id = id;
            ConferenceName = name;
            State = ConferenceState.Creating;

            c.OnChannelDtmfReceivedEvent += c_OnChannelDtmfReceivedEvent;
            c.OnBridgeCreatedEvent += c_OnBridgeCreatedEvent;
            c.OnChannelEnteredBridgeEvent += c_OnChannelEnteredBridgeEvent;
            c.OnBridgeDestroyedEvent += c_OnBridgeDestroyedEvent;
            c.OnChannelLeftBridgeEvent += c_OnChannelLeftBridgeEvent;
            c.OnRecordingFinishedEvent += c_OnRecordingFinishedEvent;

            Debug.Print("Added Conference {0}", ConferenceName);
        }

        #region ARI Events

        private void c_OnRecordingFinishedEvent(object sender, RecordingFinishedEvent e)
        {
            // Find out if this recording was for a conf user who's state is currently
            // RecordingName
            var confUser = ConferenceUsers.SingleOrDefault(x => x.CurrentRecodingId == e.Recording.Name);
            if (confUser == null) return;
            if (confUser.State != ConferenceUserState.RecordingName) return;

            confUser.State = ConferenceUserState.JoinConf;
            // Join the chanel to the bridge
            _endPoint.Bridges.AddChannel(Confbridge.Id, confUser.Channel.Id, "ConfUser");
        }

        private void c_OnChannelLeftBridgeEvent(object sender, ChannelLeftBridgeEvent e)
        {
            // left conf
            // play announcement
            _endPoint.Bridges.Play(Confbridge.Id, "recording:" + string.Format("conftemp-{0}", e.Channel.Id), "en", 0, 0);
            _endPoint.Bridges.Play(Confbridge.Id, "sound:conf-hasleft", "en", 0, 0);

            if (ConferenceUsers.Count() <= 1)
                _endPoint.Bridges.StartMoh(Confbridge.Id, "default");

            _endPoint.Recordings.DeleteStored(string.Format("conftemp-{0}-{1}", ConferenceName, e.Channel));
        }

        private void c_OnBridgeDestroyedEvent(object sender, BridgeDestroyedEvent e)
        {
            if (e.Bridge.Id == Id.ToString())
                State = ConferenceState.Destroyed;
        }

        private void c_OnChannelEnteredBridgeEvent(object sender, ChannelEnteredBridgeEvent e)
        {
            var confUser = ConferenceUsers.SingleOrDefault(x => x.Channel.Id == e.Channel.Id);
            if (confUser == null) return;

            confUser.State = ConferenceUserState.InConf;

            if (ConferenceUsers.Count(x => x.State == ConferenceUserState.InConf) > 1) // are we the only ones here
            {
                // stop moh
                _endPoint.Bridges.StopMoh(Confbridge.Id);

                // change state
                State = ConferenceState.Ready;

                // announce new user
                _endPoint.Bridges.Play(Confbridge.Id, "recording:" + confUser.CurrentRecodingId, "en", 0, 0);
                _endPoint.Bridges.Play(Confbridge.Id, "sound:conf-hasjoin", "en", 0, 0);
            }
            else
            {
                // only caller in conf
                _endPoint.Channels.Play(e.Channel.Id, "sound:conf-onlyperson", "en", 0, 0);
            }
        }

        private void c_OnBridgeCreatedEvent(object sender, BridgeCreatedEvent e)
        {
            // Never gets fired!
            if (e.Bridge.Id != Id.ToString()) return;

            State = ConferenceState.Ready;
            Debug.Print("Created bridge {0} for {1}", Id, ConferenceName);
        }

        private void c_OnChannelDtmfReceivedEvent(object sender, ChannelDtmfReceivedEvent e)
        {
            var confUser = ConferenceUsers.SingleOrDefault(x => x.Channel.Id == e.Channel.Id);
            if (confUser == null) return;

            // Pass digit to conference user
            confUser.KeyPress(e.Digit);
        }

        #endregion

        #region Public Methods

        public bool StartConference()
        {
            // TODO: fix this, .Connected returning false all the time
            //if (!client.Connected)
            //    return false;

            // Create the conference bridge
            Debug.Print("Requesting new bridge {0} for {1}", Id, ConferenceName);
            var bridge = _endPoint.Bridges.Create("mixing", Id.ToString());

            if (bridge == null)
            {
                return false;
            }


            Debug.Print("Subscribing to events on bridge {0} for {1}", Id, ConferenceName);
            _endPoint.Applications.Subscribe(AppConfig.AppName, "bridge:" + bridge.Id);

            // Start MOH on conf bridge
            _endPoint.Bridges.StartMoh(bridge.Id, "default");

            // Default state is ReadyWaiting until MOH is turned off
            State = ConferenceState.ReadyWaiting;
            Confbridge = bridge;

            // Conference ready to accept calls
            State = ConferenceState.Ready;

            return true;
        }

        public bool AddUser(Channel c)
        {
            if (State >= ConferenceState.Ready)
            {
                // Answer channel
                _endPoint.Channels.Answer(c.Id);

                // Add conference user to collection
                ConferenceUsers.Add(new ConferenceUser(this, c, _endPoint, ConferenceUserType.Normal));

                return true;
            }
            return false;
        }

        public void RemoveUser(Channel c)
        {
            // Remove the channel from the bridge
            var confUser = ConferenceUsers.SingleOrDefault(x => x.Channel.Id == c.Id);
            if (confUser == null) return;

            _endPoint.Bridges.RemoveChannel(Confbridge.Id, c.Id);
            ConferenceUsers.Remove(confUser);
        }

        public void MuteConference()
        {
            // TODO: Implement filter here to allow mute of non-admins etc
            foreach (var user in ConferenceUsers)
                _endPoint.Channels.Mute(user.Channel.Id, "in");
        }

        public void UnMuteConference()
        {
            foreach (var user in ConferenceUsers)
                _endPoint.Channels.Unmute(user.Channel.Id, "in");
        }

        public void DestroyConference()
        {
            State = ConferenceState.Destroying;

            ConferenceUsers.ForEach(x => RemoveUser(x.Channel));
            _endPoint.Bridges.Destroy(Confbridge.Id);
        }

        #endregion
    }
}