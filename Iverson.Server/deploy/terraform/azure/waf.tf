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
    condition     = contains([60, 300], var.authentik_login_rate_window)
    error_message = "authentik_login_rate_window must be one of 60 or 300 seconds, the durations Application Gateway rate-limit rules accept."
  }
}
