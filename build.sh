#!/usr/bin/env bash
set -e

build_arch() {
  echo "Building for arch: '$1'"

  rm -rf obj
  rm -rf bin

  if [ -f "spull-$1.tar.gz" ]; then
    echo "Removing old file: 'spull-$1.tar.gz'"
    rm "spull-$1.tar.gz"
  fi

  dotnet publish -c Release -r "$1" -p:PublishSingleFile=true --self-contained

  cd "bin/Release/net9.0/$1/publish"
  mv superpull spull
  if [ -x "$(command -v xattr)" ]; then
    echo 'Removing extended attributes.'
    xattr -c spull
  fi

  lastcommitdate=$(git log -1 --pretty=format:"%ad" --date=format:'%Y-%m-%dT%H:%M:%S')
  touch -d "$lastcommitdate" spull

  echo 'Creating tarball.'
  tar -cvf "../../../../../spull-$1.tar" spull
  cd ../../../../..
  echo 'Compressing tarball.'
  gzip -9 -v "spull-$1.tar"
}

build_arch 'linux-x64'
build_arch 'osx-arm64'
