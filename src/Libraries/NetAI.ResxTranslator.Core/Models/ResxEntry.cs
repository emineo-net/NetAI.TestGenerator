using System;
using System.Collections.Generic;
using System.Text;

namespace NetAI.ResxTranslator.Core.Models
{
    public class ResxEntry
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        private bool? _hasTranslation;

        public bool HasTranslation
        {
            // Nutzt den manuell gesetzten Wert, falls vorhanden; andernfalls wird gerechnet
            get => _hasTranslation ?? !string.IsNullOrWhiteSpace(Value);
            set => _hasTranslation = value;
        }
    }
}