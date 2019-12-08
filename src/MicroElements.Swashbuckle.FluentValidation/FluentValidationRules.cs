﻿using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Serialization;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MicroElements.Swashbuckle.FluentValidation
{
    /// <summary>
    /// Swagger <see cref="ISchemaFilter"/> that uses FluentValidation validators instead System.ComponentModel based attributes.
    /// </summary>
    public class FluentValidationRules : ISchemaFilter
    {
        private readonly IContractResolver _contractResolver;
        private readonly IValidatorFactory _validatorFactory;
        private readonly ILogger _logger;
        private readonly IReadOnlyList<FluentValidationRule> _rules;

        /// <summary>
        /// Creates new instance of <see cref="FluentValidationRules"/>
        /// </summary>
        /// <param name="contractResolver">Contract resolver</param>
        /// <param name="validatorFactory">Validator factory.</param>
        /// <param name="rules">External FluentValidation rules. Rule with the same name replaces default rule.</param>
        /// <param name="loggerFactory"><see cref="ILoggerFactory"/> for logging. Can be null.</param>
        public FluentValidationRules(
            [CanBeNull] IContractResolver contractResolver = null,
            [CanBeNull] IValidatorFactory validatorFactory = null,
            [CanBeNull] IEnumerable<FluentValidationRule> rules = null,
            [CanBeNull] ILoggerFactory loggerFactory = null)
        {
            _contractResolver = contractResolver;
            _validatorFactory = validatorFactory;
            _logger = loggerFactory?.CreateLogger(typeof(FluentValidationRules)) ?? NullLogger.Instance;
            _rules = CreateDefaultRules().OverrideRules(rules);
        }

        /// <inheritdoc />
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (_validatorFactory == null)
            {
                _logger.LogWarning(0, "ValidatorFactory is not provided. Please register FluentValidation.");
                return;
            }

            if (_contractResolver == null)
            {
                _logger.LogInformation(0, "ContractResolver is not provided. Using simple property names.");
            }

            IValidator validator = null;
            try
            {       
                validator = _validatorFactory.GetValidator(context.ApiModel.Type);
            }
            catch (Exception e)
            {
                _logger.LogWarning(0, e, $"GetValidator for type '{context.ApiModel.Type}' fails.");
            }

            if (validator == null)
                return;

            ApplyRulesToSchema(schema, context, validator);

            try
            {
                AddRulesFromIncludedValidators(schema, context, validator);
            }
            catch (Exception e)
            {
                _logger.LogWarning(0, e, $"Applying IncludeRules for type '{context.ApiModel.Type}' fails.");
            }
        }

        private void ApplyRulesToSchema(OpenApiSchema schema, SchemaFilterContext context, IValidator validator)
        {
            var lazyLog = new LazyLog(_logger,
                logger => logger.LogDebug($"Applying FluentValidation rules to swagger schema for type '{context.ApiModel.Type}'."));

            JsonObjectContract contract = _contractResolver?.ResolveContract(context.ApiModel.Type) as JsonObjectContract;

            foreach (var schemaPropertyName in schema?.Properties?.Keys ?? Array.Empty<string>())
            {
                var memberName = GetMemberName(contract, schemaPropertyName);

                var validators = validator.GetValidatorsForMemberIgnoreCase(memberName);

                foreach (var propertyValidator in validators)
                {
                    foreach (var rule in _rules)
                    {
                        if (rule.Matches(propertyValidator))
                        {
                            try
                            {
                                lazyLog.LogOnce();
                                rule.Apply(new RuleContext(schema, context, schemaPropertyName, propertyValidator));
                                _logger.LogDebug($"Rule '{rule.Name}' applied for property '{context.ApiModel.Type.Name}.{schemaPropertyName}'");
                            }
                            catch (Exception e)
                            {
                                _logger.LogWarning(0, e, $"Error on apply rule '{rule.Name}' for property '{context.ApiModel.Type.Name}.{schemaPropertyName}'.");
                            }
                        }
                    }
                }
            }
        }

        private string GetMemberName(JsonObjectContract contract, string key)
        {
            return contract?.Properties?.FirstOrDefault(p => p.PropertyName == key)?.UnderlyingName ?? key;
        }

        private void AddRulesFromIncludedValidators(OpenApiSchema schema, SchemaFilterContext context, IValidator validator)
        {
            // Note: IValidatorDescriptor doesn't return IncludeRules so we need to get validators manually.
            var childAdapters = (validator as IEnumerable<IValidationRule>)
                .NotNull()
                .OfType<IncludeRule>()
                .Where(includeRule => includeRule.Condition == null && includeRule.AsyncCondition == null)
                .SelectMany(includeRule => includeRule.Validators)
                .OfType<ChildValidatorAdaptor>();

            foreach (var adapter in childAdapters)
            {
                var propertyValidatorContext = new PropertyValidatorContext(new ValidationContext(null), null, string.Empty);
                var includeValidator = adapter.GetValidator(propertyValidatorContext);
                ApplyRulesToSchema(schema, context, includeValidator);
                AddRulesFromIncludedValidators(schema, context, includeValidator);
            }
        }

        /// <summary>
        /// Creates default rules.
        /// Can be overriden by name.
        /// </summary>
        public static FluentValidationRule[] CreateDefaultRules()
        {
            return new[]
            {
                new FluentValidationRule("Required")
                {
                    Matches = propertyValidator => (propertyValidator is INotNullValidator || propertyValidator is INotEmptyValidator) && propertyValidator.HasNoCondition(),
                    Apply = context =>
                    {
                        if (context.Schema.Required == null)
                            context.Schema.Required = new SortedSet<string>();
                        if(!context.Schema.Required.Contains(context.PropertyKey))
                            context.Schema.Required.Add(context.PropertyKey);
                    }
                },
                new FluentValidationRule("NotEmpty")
                {
                    Matches = propertyValidator => propertyValidator is INotEmptyValidator && propertyValidator.HasNoCondition(),
                    Apply = context =>
                    {
                        context.Schema.Properties[context.PropertyKey].MinLength = 1;
                    }
                },
                new FluentValidationRule("Length")
                {
                    Matches = propertyValidator => propertyValidator is ILengthValidator && propertyValidator.HasNoCondition(),
                    Apply = context =>
                    {
                        var lengthValidator = (ILengthValidator)context.PropertyValidator;

                        if(lengthValidator.Max > 0)
                            context.Schema.Properties[context.PropertyKey].MaxLength = lengthValidator.Max;

                        if (lengthValidator is MinimumLengthValidator
                            || lengthValidator is ExactLengthValidator
                            || context.Schema.Properties[context.PropertyKey].MinLength == null)
                            context.Schema.Properties[context.PropertyKey].MinLength = lengthValidator.Min;
                    }
                },
                new FluentValidationRule("Pattern")
                {
                    Matches = propertyValidator => propertyValidator is IRegularExpressionValidator && propertyValidator.HasNoCondition(),
                    Apply = context =>
                    {
                        var regularExpressionValidator = (IRegularExpressionValidator)context.PropertyValidator;
                        context.Schema.Properties[context.PropertyKey].Pattern = regularExpressionValidator.Expression;
                    }
                },
                new FluentValidationRule("Comparison")
                {
                    Matches = propertyValidator => propertyValidator is IComparisonValidator && propertyValidator.HasNoCondition(),
                    Apply = context =>
                    {
                        var comparisonValidator = (IComparisonValidator)context.PropertyValidator;
                        if (comparisonValidator.ValueToCompare.IsNumeric())
                        {
                            var valueToCompare = comparisonValidator.ValueToCompare.NumericToDouble();
                            var schemaProperty = context.Schema.Properties[context.PropertyKey];

                            if (comparisonValidator.Comparison == Comparison.GreaterThanOrEqual)
                            {
                                schemaProperty.Minimum = (decimal?) valueToCompare;
                            }
                            else if (comparisonValidator.Comparison == Comparison.GreaterThan)
                            {
                                schemaProperty.Minimum = (decimal?) valueToCompare;
                                schemaProperty.ExclusiveMinimum = true;
                            }
                            else if (comparisonValidator.Comparison == Comparison.LessThanOrEqual)
                            {
                                schemaProperty.Maximum = (decimal?) valueToCompare;
                            }
                            else if (comparisonValidator.Comparison == Comparison.LessThan)
                            {
                                schemaProperty.Maximum = (decimal?) valueToCompare;
                                schemaProperty.ExclusiveMaximum = true;
                            }
                        }
                    }
                },
                new FluentValidationRule("Between")
                {
                    Matches = propertyValidator => propertyValidator is IBetweenValidator && propertyValidator.HasNoCondition(),
                    Apply = context =>
                    {
                        var betweenValidator = (IBetweenValidator)context.PropertyValidator;
                        var schemaProperty = context.Schema.Properties[context.PropertyKey];

                        if (betweenValidator.From.IsNumeric())
                        {
                            schemaProperty.Minimum = (decimal?) betweenValidator.From.NumericToDouble();

                            if (betweenValidator is ExclusiveBetweenValidator)
                            {
                                schemaProperty.ExclusiveMinimum = true;
                            }
                        }

                        if (betweenValidator.To.IsNumeric())
                        {
                            schemaProperty.Maximum = (decimal?) betweenValidator.To.NumericToDouble();

                            if (betweenValidator is ExclusiveBetweenValidator)
                            {
                                schemaProperty.ExclusiveMaximum = true;
                            }
                        }
                    }
                },
            };
        }
    }
}