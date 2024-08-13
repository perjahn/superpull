#!/usr/bin/env bash
set -e
rm -rf obj
rm -rf bin
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained
ls -laR bin
