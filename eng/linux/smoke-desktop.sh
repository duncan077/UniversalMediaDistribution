#!/bin/sh
set -eu
mkdir -p /tmp/umd /tmp/umd-home
cp -R /smoke/. /tmp/umd/
cp -R /bundle/. /tmp/umd/
chmod +x /tmp/umd/UniversalMediaDistribution.Linux.SmokeTests
export HOME=/tmp/umd-home
export GDK_BACKEND=x11
# Docker Desktop can inject host proxy settings. Fixtures must stay on the
# container's loopback even though video validation uses a non-IP host name.
export NO_PROXY='*' no_proxy='*' HTTP_PROXY='' http_proxy='' HTTPS_PROXY='' https_proxy='' ALL_PROXY='' all_proxy=''
# Docker does not permit WebKit's nested user-namespace sandbox. This override
# belongs only to this disposable test container serving loopback fixtures;
# it must never be added to the desktop app or distribution launcher.
export WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1
exec dbus-run-session -- xvfb-run -a /tmp/umd/UniversalMediaDistribution.Linux.SmokeTests
