# SonosControl
[![Dockerhub](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml/badge.svg)](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml)

https://hub.docker.com/r/darkatek7/sonoscontrol
____

## Docker Compose
```
---
version: '3.4'
services:
  sonos:
    container_name: sonos
    image: darkatek7/sonoscontrol:latest        # docker image
    ports:
      - 80:80                                   # port used for localhost ip
    restart: unless-stopped                     # restart policy
    # environment:
    #   - TZ:"Europe/Vienna"
    volumes:
      - ./Data:/app/Data
```
