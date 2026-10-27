terraform {
  required_version = ">= 1.7"
  required_providers {
    aws = { source = "hashicorp/aws", version = "~> 5.0" }
  }
}

variable "region" {
  type    = string
  default = "us-east-1"
}

variable "state_bucket_name" {
  type    = string
  default = "iverson-terraform-state"
}

variable "lock_table_name" {
  type    = string
  default = "iverson-terraform-locks"
}

provider "aws" {
  region = var.region
}

resource "aws_s3_bucket" "state" {
  bucket = var.state_bucket_name
}

resource "aws_s3_bucket_versioning" "state" {
  bucket = aws_s3_bucket.state.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "state" {
  bucket = aws_s3_bucket.state.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_public_access_block" "state" {
  bucket                  = aws_s3_bucket.state.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_dynamodb_table" "locks" {
  name         = var.lock_table_name
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "LockID"

  attribute {
    name = "LockID"
    type = "S"
  }
}

# CSR round 12 §4.4: the pin-freshness workflow (.github/workflows/pin-freshness.yml) reads which EKS add-on
# versions are compatible with the cluster's Kubernetes version. It signs in with GitHub's OIDC token, as a role
# only main's runs of this repository can assume, holding only eks:DescribeAddonVersions. An account has one
# OIDC provider per URL: if it already has GitHub's, import it before the first apply
# (docs/runbooks/csr12-supply-chain.md).
resource "aws_iam_openid_connect_provider" "github" {
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
}

resource "aws_iam_role" "pin_freshness" {
  name = "iverson-pin-freshness"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Federated = aws_iam_openid_connect_provider.github.arn }
      Action    = "sts:AssumeRoleWithWebIdentity"
      Condition = {
        StringEquals = {
          "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com"
          "token.actions.githubusercontent.com:sub" = "repo:counsellorben/Iverson:ref:refs/heads/main"
        }
      }
    }]
  })
}

resource "aws_iam_role_policy" "pin_freshness" {
  name = "describe-addon-versions"
  role = aws_iam_role.pin_freshness.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = "eks:DescribeAddonVersions"
      Resource = "*"
    }]
  })
}

output "state_bucket_name" { value = aws_s3_bucket.state.bucket }
output "lock_table_name" { value = aws_dynamodb_table.locks.name }
# Set as the repository variable PIN_FRESHNESS_AWS_ROLE.
output "pin_freshness_role_arn" { value = aws_iam_role.pin_freshness.arn }
