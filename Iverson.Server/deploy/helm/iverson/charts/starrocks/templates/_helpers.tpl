{{- /*
CSR round 11 #4: where the FE keystore lives and its password. The init container in
starrockscluster.yaml writes the keystore and configmap-fe.yaml's fe.conf points FE at it, so both
read these two values from here.

The password is a fixed constant, not a secret: the keystore exists only on the FE pod's emptyDir
and holds the same key <release>-starrocks-tls already protects, so a password adds no protection,
and a generated one would have to be copied into the ConfigMap.
*/}}
{{- define "starrocks.feKeystorePath" -}}
/opt/starrocks/fe/tls/fe.p12
{{- end }}

{{- define "starrocks.feKeystorePassword" -}}
iverson-fe-keystore
{{- end }}
