# CSR round 12 §4.3: every image .gitlab-ci.yml runs, one FROM each, so Dependabot's docker ecosystem proposes
# their bumps (tag and digest). Never built. ci/pins.py consistency (deploy-validate.yml) keeps .gitlab-ci.yml
# equal to this file, so a Dependabot bump here stays red until the same pull request copies it there.
FROM alpine/helm:3.15.4@sha256:105741fa6621ed9a3ea944066de78bb27d4b9bb93a56ce8e7cb4d621e1e4bbf2
FROM hashicorp/terraform:1.7.5@sha256:386b7bff108f9fd3b79a2e0110190c162b5e4aebf26afe3eef028fd89532c17e
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
FROM node:22@sha256:0e5f906573693feaa1e21057ebdcfdb5bd5021f050b2dc7c9deceb629c7da2a8
FROM golang:1.26@sha256:d7722066f0b60ceccb6c0643cbed1f5f9e15506ac237146c95333504d7805d89
FROM maven:3.9-eclipse-temurin-21@sha256:99e61abcff91a9b1333463bd8451fb18495d6eba9250ac66a338b518f8278320
FROM python:3.11@sha256:50cfccc707e8721201ee018bfd7f6a85d8eacf49c4f9f75833a2aba09a10411f
