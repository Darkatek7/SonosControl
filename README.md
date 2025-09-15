# SonosControl
[![Dockerhub](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml/badge.svg)](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml)

https://hub.docker.com/r/darkatek7/sonoscontrol
____

## Description
This self-hosted application automates turning a Sonos speaker on and off at daily start and stop times (UTC) set in the config file or the UI.
The app also supports playing predefined TuneIn stations or Spotify songs, playlists, and albums.
____

## What's New
### Version July 2025
- ✅ **User Authentication Added**  
  You can now register and log in to the app using a simple authentication system.  
  ➤ Visit [`/register`](http://localhost:8080/register) to create new user accounts.

- 🛠 Improved UI and Station Lookup
- 🔧 Minor bug fixes and backend optimizations
- 📱 Mobile friendly Design

> Want to see what’s coming next? Check the [TODO section](#todo) below.

___

## Info
* Blazor Server Application (.Net 9)
* Docker
____

## UI
### Index
<img width="940" height="1066" alt="image" src="https://github.com/user-attachments/assets/44a9e2a2-dcf2-4bda-bf45-eb289ff34472" />


### Config
![image](https://github.com/user-attachments/assets/88e804df-c6a1-458e-8ed3-0f1ddef2d4ee)

### Station Lookup
<img width="977" height="1581" alt="image" src="https://github.com/user-attachments/assets/170d5aeb-a887-4e93-be20-f66020cad955" />



____
## Usage
### Docker Compose ([click here for more info](https://docs.linuxserver.io/general/docker-compose))

Create a file called docker-compose.yml.

Ubuntu/Debian:
```
nano docker-compose.yml
```

Paste following code:
```
---
version: '3.4'
services:
  sonos:
    container_name: sonos
    image: darkatek7/sonoscontrol:latest
    ports:
      - 8080:8080
    restart: unless-stopped
    environment:
      - TZ=Europe/Vienna
    volumes:
      - ./Data:/app/Data
      # persist data‑protection keys
      - ./dpkeys:/root/.aspnet/DataProtection-Keys
```

Login using the default admin user:
```
admin
```

password:
```
ESPmtZ7&LW2z&xHF
```

The default user is seeded with both **admin** and **superadmin** roles. Only a superadmin can assign roles to other users, and superadmin accounts cannot be deleted by non-superadmin users.

### *This is optional. You only have to do this if you get a config.json error for some reason.*

At first create a folder called **Data**.

Ubuntu/Debian:
```
mkdir Data
```

Now create a file called **config.json**

Ubuntu/Debian:
```
nano Data/config.json
```
and paste the following:
```
{
  "Volume": 15,
  "StartTime": "06:45:00",
  "StopTime": "17:30:00",
  "IP_Adress": "10.0.0.1",
  "Stations": [
    {
      "Name": "Antenne Vorarlberg",
      "Url": "web.radio.antennevorarlberg.at/av-live/stream/mp3"
    },
    {
      "Name": "Radio V",
      "Url": "orf-live.ors-shoutcast.at/vbg-q2a"
    },
    {
      "Name": "Rock Antenne Bayern",
      "Url": "stream.rockantenne.bayern/80er-rock/stream/mp3"
    },
    {
      "Name": "Kronehit",
      "Url": "onair.krone.at/kronehit.mp3"
    },
    {
      "Name": "Ö3",
      "Url": "orf-live.ors-shoutcast.at/oe3-q2a"
    },
    {
      "Name": "Radio Paloma",
      "Url": "www3.radiopaloma.de/RP-Hauptkanal.pls"
    },
    {
      "Name": "365 days christmas",
      "Url": "us3.streamingpulse.com/ssl/7038"
    },
    {
      "Name": "__BREAKZ.FM__ by rautemusik (rm.fm)",
      "Url": "breakz-high.rautemusik.fm/"
    }
  ],
  "SpotifyTracks": [
    {
      "Name": "Top 50 Global",
      "Url": "https://open.spotify.com/playlist/37i9dQZEVXbMDoHDwVN2tF"
    },
    {
      "Name": "Astroworld",
      "Url": "https://open.spotify.com/album/41GuZcammIkupMPKH2OJ6I"
    }
  ]
}
```
You can change the config to fit your needs or change it later in the UI.


## TODO
- [x] Add user authentication  
- [x] Password Reset Page
- [x] Add option to change the days which the automation should run  
- [x] Add a "play this song/playlist/station" on startup  
- [ ] Add play random station on startup toggle  
- [ ] Support custom playback durations (e.g., "play for 1 hour after start time")
- [ ] Enable shuffle toggle for Spotify and Stations
- [x] Add logs tab to show history of play/stop events with user associated
- [x] Enable / disable user registration
- [x] Add role-based access (e.g., admin vs. operator)
