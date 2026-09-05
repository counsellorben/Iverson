{{/*
Resolve the active embedding-model entry from global.embeddingModels, as YAML for `fromYaml`.
One helper rather than an inline lookup per subchart: api and worker must not be able to
implement the resolution differently. A name matching no entry emits nothing, which fromYaml
yields as an empty dict — handled by the `default` on Embeddings__ModelId at the call sites.
*/}}
{{- define "iverson.activeEmbeddingModel" -}}
{{- $name := .Values.global.activeEmbeddingModel -}}
{{- range .Values.global.embeddingModels -}}
{{- if eq .name $name -}}
{{- toYaml . -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
Emit the Embeddings__* env entries for the active embedding model, resolved via
iverson.activeEmbeddingModel. One helper rather than a copy per subchart: api and worker must
not be able to implement the emission differently. Callers pipe this through nindent, so the
body carries no leading indentation of its own.
*/}}
{{- define "iverson.embeddingEnv" -}}
{{- $active := include "iverson.activeEmbeddingModel" . | fromYaml -}}
- name: Embeddings__ModelId
  value: {{ $active.name | default .Values.global.activeEmbeddingModel | quote }}
{{- if hasKey $active "documentPrefix" }}
- name: Embeddings__DocumentPrefix
  value: {{ $active.documentPrefix | quote }}
{{- end }}
{{- if hasKey $active "queryPrefix" }}
- name: Embeddings__QueryPrefix
  value: {{ $active.queryPrefix | quote }}
{{- end }}
{{- range $i, $m := .Values.global.embeddingModels }}
- name: Embeddings__Models__{{ $i }}__Name
  value: {{ $m.name | quote }}
- name: Embeddings__Models__{{ $i }}__BaseUrl
  value: "http://{{ $.Release.Name }}-tei-{{ $m.slug }}:8080"
{{- end }}
{{- end -}}

{{/*
The global embedding fallback: the first tei entry's headless Service, on the container port.
*/}}
{{- define "iverson.embeddingBaseUrl" -}}
{{- $first := index .Values.global.embeddingModels 0 -}}
http://{{ .Release.Name }}-tei-{{ $first.slug }}:8080
{{- end -}}
