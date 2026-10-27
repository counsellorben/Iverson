# CSR round 12 §4.3: every image .gitlab-ci.yml runs, one FROM each, so Dependabot's docker ecosystem proposes
# their bumps (tag and digest). Never built. ci/pins.py consistency (deploy-validate.yml) keeps .gitlab-ci.yml
# equal to this file, so a Dependabot bump here stays red until the same pull request copies it there.
FROM alpine/helm:3.15.4@sha256:105741fa6621ed9a3ea944066de78bb27d4b9bb93a56ce8e7cb4d621e1e4bbf2
FROM hashicorp/terraform:1.7.5@sha256:386b7bff108f9fd3b79a2e0110190c162b5e4aebf26afe3eef028fd89532c17e
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
FROM node:22@sha256:0e5f906573693feaa1e21057ebdcfdb5bd5021f050b2dc7c9deceb629c7da2a8
FROM golang:1.26@sha256:d7722066f0b60ceccb6c0643cbed1f5f9e15506ac237146c95333504d7805d89
FROM maven:3.9-eclipse-temurin-26@sha256:b2c1ad85954592f9928e84327c65201f308ad9b5d8ed7d5b823717c97bf23fbb
FROM python:3.11@sha256:50cfccc707e8721201ee018bfd7f6a85d8eacf49c4f9f75833a2aba09a10411f
