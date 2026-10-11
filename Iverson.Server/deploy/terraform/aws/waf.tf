# CSR round-11 #5: the edge rate limit on Authentik's flow executor, the path every login and MFA
# submission uses. Counted per client address; requests over the limit in the window are blocked.
variable "authentik_login_rate_limit" {
  type    = number
  default = 100
}

variable "authentik_login_rate_window" {
  type    = number
  default = 300
  validation {
    condition     = contains([60, 120, 300, 600], var.authentik_login_rate_window)
    error_message = "authentik_login_rate_window must be one of 60, 120, 300 or 600 seconds, the windows WAF rate rules accept."
  }
}

# One ACL, attached only to the Authentik Ingress's own ALB (authentik.ingress.wafAclArn; each
# Ingress gets its own ALB). The path is URL-decoded before it is matched, because Authentik decodes
# percent-encoded path segments and serves the executor for them.
resource "aws_wafv2_web_acl" "authentik_login" {
  name  = "${var.cluster_name}-authentik-login"
  scope = "REGIONAL"

  default_action {
    allow {}
  }

  rule {
    name     = "authentik-login-rate"
    priority = 1

    action {
      block {}
    }

    statement {
      rate_based_statement {
        limit                 = var.authentik_login_rate_limit
        aggregate_key_type    = "IP"
        evaluation_window_sec = var.authentik_login_rate_window

        scope_down_statement {
          byte_match_statement {
            search_string         = "/api/v3/flows/executor/"
            positional_constraint = "STARTS_WITH"
            field_to_match {
              uri_path {}
            }
            text_transformation {
              priority = 0
              type     = "URL_DECODE"
            }
          }
        }
      }
    }

    visibility_config {
      cloudwatch_metrics_enabled = true
      metric_name                = "authentik-login-rate"
      sampled_requests_enabled   = true
    }
  }

  visibility_config {
    cloudwatch_metrics_enabled = true
    metric_name                = "${var.cluster_name}-authentik-login"
    sampled_requests_enabled   = true
  }
}

# Copy into values-aws.yaml's authentik.ingress.wafAclArn (docs/runbooks/csr11-identity-plane.md).
output "authentik_waf_acl_arn" { value = aws_wafv2_web_acl.authentik_login.arn }
