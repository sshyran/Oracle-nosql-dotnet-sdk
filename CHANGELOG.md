# Change Log

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/).

## [5.1.3]

**Added**

* Added new regions for cloud service: jnb, wga, sin, mrs, arn, mct, auh.

**Fixed**

* Fixed a bug in IAMAuthorizationProvider where local time was used instead of
universal time to determine the validity of the signature causing the
connection to fail in some time zones.

## [5.1.2]

This is the initial release of Oracle NoSQL SDK for .NET.
