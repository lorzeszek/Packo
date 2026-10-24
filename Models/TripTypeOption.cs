using Packo.Models.Enums;
using Packo.Services;

namespace Packo.Models
{
    public class TripTypeOption
    {
        private readonly string _resourceKey;

        public TripType Value { get; }
        public string Name => LocalizationResourceManager.Instance[_resourceKey];

        public TripTypeOption(TripType value, string resourceKey)
        {
            Value = value;
            _resourceKey = resourceKey;
        }
    }
}
