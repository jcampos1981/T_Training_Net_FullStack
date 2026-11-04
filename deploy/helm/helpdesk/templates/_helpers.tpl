{{/* Las etiquetas que tiene todo lo de una aplicación. El selector usa solo las dos primeras, que no cambian entre versiones. */}}
{{- define "helpdesk.selectorLabels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/part-of: helpdesk
{{- end }}

{{- define "helpdesk.labels" -}}
{{ include "helpdesk.selectorLabels" . }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/version: {{ .root.Values.image.tag | quote }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end }}

{{/* registro/nombre:tag. Sin tag, la instalación falla con un mensaje claro en vez de bajar "latest". */}}
{{- define "helpdesk.image" -}}
{{ .root.Values.image.registry }}/{{ .image }}:{{ required "Falta image.tag: un sha-<commit> de los que publica CI." .root.Values.image.tag }}
{{- end }}

{{/* Variables que salen de una llave del Secret, a partir de un mapa VARIABLE: llave. */}}
{{- define "helpdesk.secretEnv" -}}
{{- $secret := .secret -}}
{{- range $variable, $key := .values }}
- name: {{ $variable }}
  valueFrom:
    secretKeyRef:
      name: {{ $secret }}
      key: {{ $key }}
{{- end }}
{{- end }}
