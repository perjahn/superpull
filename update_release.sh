#!/usr/bin/env bash
set -e

baseurl="https://api.github.com/repos/$GITHUB_REPOSITORY"
baseurluploads="https://uploads.github.com/repos/$GITHUB_REPOSITORY"
accept="Accept: application/vnd.github+json"
auth="Authorization: Bearer $1"
apiversion="X-GitHub-Api-Version: 2022-11-28"
contenttype="Content-Type: application/zip"

echo 'Retrieving release...'
latest=$(curl -s -H "$accept" -H "$auth" -H "$apiversion" "$baseurl/releases/latest")

if [ ! -z "$latest" ]; then
  RELEASE_ID=$(jq -r '.id' <<< "$latest")
  echo "Got release id: '"$RELEASE_ID"'"

  if [ ! -z "$RELEASE_ID" ] && [ "$RELEASE_ID" != "null" ]; then
    echo 'Deleting release...'
    curl -s -X DELETE -H "$accept" -H "$auth" -H "$apiversion" "$baseurl/releases/$RELEASE_ID" > /dev/null
  fi
fi

json='{"tag_name":"v1.0.'"$GITHUB_RUN_NUMBER"'","name":"v1.0.'"$GITHUB_RUN_NUMBER"'"}'

echo 'Creating new release...'
newrelease=$(curl -s -X POST -H "$accept" -H "$auth" -H "$apiversion" "$baseurl/releases" -d "$json")

if [ -z "$newrelease" ]; then
  echo "Couldn't get release id."
  exit 1
fi

RELEASE_ID=$(jq -r '.id' <<< "$newrelease")
echo "Got release id: '"$RELEASE_ID"'"

if [ -z "$RELEASE_ID" ] || [ "$RELEASE_ID" = "null" ]; then
  echo "Couldn't get release id."
  exit 1
fi

echo 'Uploading assets...'
curl -s -X POST -H "$accept" -H "$auth" -H "$apiversion" "$baseurluploads/releases/$RELEASE_ID/assets?name=spull-linux-x64.tar.gz" --data-binary "@spull-linux-x64.tar.gz" -H "$contenttype" > /dev/null
curl -s -X POST -H "$accept" -H "$auth" -H "$apiversion" "$baseurluploads/releases/$RELEASE_ID/assets?name=spull-osx-arm64.tar.gz" --data-binary "@spull-osx-arm64.tar.gz" -H "$contenttype" > /dev/null

echo 'Done!'
