import { useId } from 'react'

interface Option {
  value: string
  label: string
}

interface FormFieldProps {
  label: string
  value: string
  onChange: (value: string) => void
  /** Message from the server (or the form) about this field. */
  error?: string
  type?: string
  /** When given, the field is a drop-down list instead of a text box. */
  options?: Option[]
  /** For a drop-down: the first, empty choice, such as "Choose a customer". */
  placeholder?: string
  disabled?: boolean
  min?: number
  step?: string
  autoComplete?: string
}

/** A labelled input or select with its error message. Used by every form in the app. */
export function FormField({ label, value, onChange, error, type = 'text', options, placeholder, disabled, min, step, autoComplete }: FormFieldProps) {
  const id = useId()
  const errorId = `${id}-error`
  const common = {
    id,
    value,
    disabled,
    'aria-invalid': error ? true : undefined,
    'aria-describedby': error ? errorId : undefined,
  }

  return (
    <div className="form-field">
      <label htmlFor={id}>{label}</label>
      {options ? (
        <select {...common} onChange={(event) => onChange(event.target.value)}>
          <option value="">{placeholder ?? 'Select…'}</option>
          {options.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      ) : (
        <input {...common} type={type} min={min} step={step} autoComplete={autoComplete} onChange={(event) => onChange(event.target.value)} />
      )}
      {error && (
        <p className="form-field__error" id={errorId}>
          {error}
        </p>
      )}
    </div>
  )
}
