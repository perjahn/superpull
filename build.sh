#!/usr/bin/env bash
rm -rf obj
rm -rf bin
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained
