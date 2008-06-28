#region License

/*
 * Copyright 2002-2004 the original author or authors.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#endregion

#region Imports

using System;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Web;
#if NET_2_0
using System.Web.Compilation;
#endif
using System.Web.UI;
using Spring.Collections;
using Spring.Context;
using Spring.Context.Support;
using Spring.Core;
using Spring.DataBinding;
using Spring.Globalization;
using Spring.Validation;
using Spring.Web.Support;
using IValidator = Spring.Validation.IValidator;

#endregion

namespace Spring.Web.UI
{
    /// <summary>
    /// Extends standard .Net user control by adding data binding and localization functionality.
    /// </summary>
    /// <author>Aleksandar Seovic</author>
    public class UserControl : System.Web.UI.UserControl, IApplicationContextAware, IWebDataBound, ISupportsWebDependencyInjection,
                               IPostBackDataHandler,IValidationContainer
    {
        #region Static fields

        private static readonly object EventPreLoadViewState = new object();
        private static readonly object EventDataBindingsInitialized = new object();
        private static readonly object EventDataBound = new object();
        private static readonly object EventDataUnbound = new object();

        #endregion

        #region Instance Fields

        private object controller;
        private ILocalizer localizer;
        private IMessageSource messageSource;
        private IDictionary sharedState;
        private IBindingContainer bindingManager;
        private IValidationErrors validationErrors = new ValidationErrors();
        private IDictionary results;
        private IApplicationContext applicationContext;
        private IApplicationContext defaultApplicationContext;
        private bool needsUnbind = false;

        #endregion

        #region Control lifecycle methods

#if !NET_2_0
        /// <summary>
        /// Gets a value indicating whether this instance is in design mode.
        /// </summary>
        /// <value>
        /// 	<c>true</c> if this instance is in design mode; otherwise, <c>false</c>.
        /// </value>
        protected bool DesignMode
        {
            get { return this.Context == null; }
        }

        /// <summary>
        /// Gets a value indicating whether view state is enabled for this page.
        /// </summary>
        protected internal bool IsViewStateEnabled
        {
            get
            {
                for (Control parent = this; parent != null; parent = parent.Parent)
                {
                    if (!parent.EnableViewState)
                    {
                        return false;
                    }
                }
                return true;
            }
        }
#endif

        /// <summary>
        /// Initializes user control.
        /// </summary>
        protected override void OnInit(EventArgs e)
        {
            InitializeMessageSource();
            InitializeBindingManager();

            if (!IsPostBack)
            {
                InitializeModel();
            }
            else
            {
                LoadModel(LoadModelFromPersistenceMedium());
            }

            base.OnInit(e);

            OnInitializeControls(EventArgs.Empty);
        }

        /// <summary>
        /// Raises the <see cref="PreLoadViewState"/> event after page initialization.
        /// </summary>
        protected internal virtual void OnPreLoadViewState(EventArgs e)
        {
            EventHandler handler = (EventHandler) base.Events[EventPreLoadViewState];
            if (handler != null)
            {
                handler(this, e);
            }
        }

        /// <summary>
        /// PreLoadViewState event.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This event is raised if <see cref="System.Web.UI.Page.IsPostBack"/> is true
        /// immediately before state is restored from ViewState.
        /// </para>
        /// <para>
        /// NOTE: Different from <see cref="System.Web.UI.Control.LoadViewState(object)"/>, this event will always be raised!
        /// </para>
        /// </remarks>
        public event EventHandler PreLoadViewState
        {
            add { base.Events.AddHandler(EventPreLoadViewState, value); }
            remove { base.Events.RemoveHandler(EventPreLoadViewState, value); }
        }

        /// <summary>
        /// This method is called during a postback if this control has been visible when being rendered to the client.
        /// </summary>
        /// <remarks>
        /// If the controls has been visible when being rendering to the client, <see cref="System.Web.UI.Page.RegisterRequiresPostBack"/>
        /// has been called during <see cref="OnPreRender"/>
        /// </remarks>
        /// <returns>true if the server control's state changes as a result of the post back; otherwise false.</returns>
        bool IPostBackDataHandler.LoadPostData(string postDataKey, NameValueCollection postCollection)
        {
            return LoadPostData(postDataKey, postCollection);
        }

        /// <summary>
        /// This method is called during a postback if this control has been visible when being rendered to the client.
        /// </summary>
        /// <returns>true if the server control's state changes as a result of the post back; otherwise false.</returns>
        protected virtual bool LoadPostData(string postDataKey, NameValueCollection postCollection)
        {
            // mark this control for unbinding form data during OnLoad()
            this.needsUnbind = true;
            return false;
        }

        /// <summary>
        /// When implemented by a class, signals the server control object to notify the
        /// ASP.NET application that the state of the control has changed.
        /// </summary>
        void IPostBackDataHandler.RaisePostDataChangedEvent()
        {
            RaisePostDataChangedEvent();
        }

        /// <summary>
        /// When implemented by a class, signals the server control object to notify the
        /// ASP.NET application that the state of the control has changed.
        /// </summary>
        protected virtual void RaisePostDataChangedEvent()
        {
            return;
        }


        /// <summary>
        /// First unbinds data from the controls into a data model and
        /// then raises Load event in order to execute all associated handlers.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected override void OnLoad(EventArgs e)
        {
            if (IsPostBack && needsUnbind)
            {
                // unbind form data
                UnbindFormData();
            }
            base.OnLoad(e);
        }

        /// <summary>
        /// Binds data from the data model into controls and raises
        /// PreRender event afterwards.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected override void OnPreRender(EventArgs e)
        {
            if (Visible)
            {
                // causes IPostBackDataHandler.LoadPostData() to be called on next postback.
                // this is used for indicating a required call to UnbindFormData()
                Page.RegisterRequiresPostBack(this);

                BindFormData();

                if (localizer != null)
                {
                    localizer.ApplyResources(this, messageSource, UserCulture);
                }
                else if (Page.Localizer != null)
                {
                    Page.Localizer.ApplyResources(this, messageSource, UserCulture);
                }
            }

            base.OnPreRender(e);

            object modelToSave = SaveModel();
            if (modelToSave != null)
            {
                SaveModelToPersistenceMedium(modelToSave);
            }
        }

        /// <summary>
        /// This event is raised before Load event and should be used to initialize
        /// controls as necessary.
        /// </summary>
        public event EventHandler InitializeControls;

        /// <summary>
        /// Raises InitializeControls event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnInitializeControls(EventArgs e)
        {
            if (InitializeControls != null)
            {
                InitializeControls(this, e);
            }
        }

        /// <summary>
        /// Obtains a <see cref="T:System.Web.UI.UserControl"/> object from a user control file
        /// and injects dependencies according to Spring config file.
        /// </summary>
        /// <param name="virtualPath">The virtual path to a user control file.</param>
        /// <returns>
        /// Returns the specified <see langword="UserControl"/> object, with dependencies injected.
        /// </returns>
        protected new Control LoadControl(string virtualPath)
        {
            Control control = base.LoadControl(virtualPath);
            WebDependencyInjectionUtils.InjectDependenciesRecursive(defaultApplicationContext, control);
            return control;
        }

#if NET_2_0
        /// <summary>
        /// Obtains a <see cref="T:System.Web.UI.UserControl"/> object by type
        /// and injects dependencies according to Spring config file.
        /// </summary>
        /// <param name="t">The type of a user control.</param>
        /// <param name="parameters">parameters to pass to the control</param>
        /// <returns>
        /// Returns the specified <see langword="UserControl"/> object, with dependencies injected.
        /// </returns>
        protected new Control LoadControl( Type t, params object[] parameters)
        {
            Control control = base.LoadControl( t, parameters );
            WebDependencyInjectionUtils.InjectDependenciesRecursive(defaultApplicationContext, control);
            return control;
        }
#endif

        #endregion

        #region Model Management Support

        /// <summary>
        /// Initializes data model when the page is first loaded.
        /// </summary>
        /// <remarks>
        /// This method should be overriden by the developer
        /// in order to initialize data model for the page.
        /// </remarks>
        protected virtual void InitializeModel()
        {
        }

        /// <summary>
        /// Retrieves data model from a persistence store.
        /// </summary>
        /// <remarks>
        /// The default implementation uses <see cref="System.Web.UI.Page.Session"/> to store and retrieve
        /// the model for the current <see cref="System.Web.HttpRequest.CurrentExecutionFilePath" />
        /// </remarks>
        protected virtual object LoadModelFromPersistenceMedium()
        {
            return Session[Request.CurrentExecutionFilePath + this.UniqueID + ".Model"];
        }

        /// <summary>
        /// Saves data model to a persistence store.
        /// </summary>
        /// <remarks>
        /// The default implementation uses <see cref="System.Web.UI.Page.Session"/> to store and retrieve
        /// the model for the current <see cref="System.Web.HttpRequest.CurrentExecutionFilePath" />
        /// </remarks>
        protected virtual void SaveModelToPersistenceMedium(object modelToSave)
        {
            Session[Request.CurrentExecutionFilePath + this.UniqueID + ".Model"] = modelToSave;
        }

        /// <summary>
        /// Loads the saved data model on postback.
        /// </summary>
        /// <remarks>
        /// This method should be overriden by the developer
        /// in order to load data model for the page.
        /// </remarks>
        protected virtual void LoadModel(object savedModel)
        {
        }

        /// <summary>
        /// Returns a model object that should be saved.
        /// </summary>
        /// <remarks>
        /// This method should be overriden by the developer
        /// in order to save data model for the page.
        /// </remarks>
        /// <returns>
        /// A model object that should be saved.
        /// </returns>
        protected virtual object SaveModel()
        {
            return null;
        }

        #endregion<

        #region Controller Support

        /// <summary>
        /// Gets or sets controller for the control.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Internally calls are delegated to <see cref="GetController"/> and <see cref="SetController"/>.
        /// </para>
        /// </remarks>
        /// <value>Controller for the control.</value>
        public object Controller
        {
            get { return GetController(); }
            set { SetController(value); }
        }

        /// <summary>
        /// <para>Stores the controller to be returned by <see cref="Controller"/> property.</para>
        /// </summary>
        /// <remarks>
        /// The default implementation uses a field to store the reference. Derived classes may override this behaviour
        /// but must ensure to also change the behaviour of <see cref="GetController"/> accordingly.
        /// </remarks>
        /// <param name="controller">Controller for the control.</param>
        protected virtual void SetController(object controller)
        {
            this.controller = controller;
        }

        /// <summary>
        /// <para>Returns the controller stored by <see cref="SetController"/>.</para>
        /// </summary>
        /// <remarks>
        /// <para>
        /// The default implementation uses a field to retrieve the reference.
        /// </para>
        /// <para>
        /// If external controller is not specified, control will serve as its own controller,
        /// which will allow data binding to work properly.
        /// </para>
        /// <para>
        /// You may override this method e.g. to return <see cref="Spring.Web.UI.Page.Controller"/> in order to
        /// have your control bind to the same controller as your page. When overriding this behaviour, derived classes
        /// must ensure to also change the behaviour of <see cref="SetController"/> accordingly.
        /// </para>
        /// </remarks>
        /// <returns>
        /// <para>The controller for this control.</para>
        /// <para>If no controller is set, a reference to the control itself is returned.</para>
        /// </returns>
        protected virtual object GetController()
        {
            if(controller == null)
            {
                return this;
            }
            return controller;
        }

        #endregion Controller Support

        #region Shared State support

        /// <summary>
        /// Returns a thread-safe dictionary that contains state that is shared by
        /// all instances of this control.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        protected IDictionary SharedState
        {
            get
            {
                if (sharedState == null)
                {
                    string thisTypeKey = this.GetType().FullName + this.GetType().GetHashCode() + ".SharedState";

                    sharedState = Application[thisTypeKey] as IDictionary;
                    if (sharedState==null)
                    {
                        Application.Lock();
                        try
                        {
                            sharedState = Application[thisTypeKey] as IDictionary;
                            if (sharedState == null)
                            {
                                sharedState = new SynchronizedHashtable();
                                Application.Add(thisTypeKey, sharedState);
                            }
                        }
                        finally
                        {
                            Application.UnLock();
                        }
                    }
                }
                return sharedState;
            }
        }

        #endregion Shared State support

        #region Result support

        /// <summary>
        /// Gets or sets map of result names to target URLs
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public IDictionary Results
        {
            get
            {
                if (results == null)
                {
                    results = new Hashtable();
                }
                return results;
            }
            set
            {
                results = new Hashtable();
                foreach (DictionaryEntry entry in value)
                {
                    if (entry.Value is Result)
                    {
                        results[entry.Key] = entry.Value;
                    }
                    else if (entry.Value is String)
                    {
                        results[entry.Key] = new Result((string)entry.Value);
                    }
                    else
                    {
                        throw new TypeMismatchException(
                            "Unable to create result object. Please use either String or Result instances to define results.");
                    }
                }
            }
        }

        /// <summary>
        /// Redirects user to a URL mapped to specified result name.
        /// </summary>
        /// <param name="resultName">Result name.</param>
        protected void SetResult(string resultName)
        {
            Result result = (Result)Results[resultName];
            if (result == null)
            {
                // bubble up result in the hierarchy
                Control parent = this.Parent;
                while(parent != null)
                {
                    if (parent is UserControl)
                    {
                        ((UserControl)parent).SetResult(resultName);
                        return;
                    }
                    else if (parent is Page)
                    {
                        ((Page) parent).SetResult(resultName);
                        return;
                    }
                    parent = parent.Parent;
                }
                throw new ArgumentException(string.Format("No URL mapping found for the specified result '{0}'.", resultName), "resultName");
            }

            result.Navigate(this);
        }

        /// <summary>
        /// Redirects user to a URL mapped to specified result name.
        /// </summary>
        /// <param name="resultName">Result name.</param>
        /// <param name="context">The context to use for evaluating the SpEL expression in the Result.</param>
        protected void SetResult(string resultName, object context)
        {
            Result result = (Result)Results[resultName];
            if (result == null)
            {
                // bubble up result in the hierarchy
                Control parent = this.Parent;
                while (parent != null)
                {
                    if (parent is UserControl)
                    {
                        ((UserControl)parent).SetResult(resultName, context);
                        return;
                    }
                    else if (parent is Page)
                    {
                        ((Page)parent).SetResult(resultName, context);
                        return;
                    }
                    parent = parent.Parent;
                }
                throw new ArgumentException(string.Format("No URL mapping found for the specified result '{0}'.", resultName), "resultName");
            }

            result.Navigate(context);
        }

        #endregion

        #region Validation support

        /// <summary>
        /// Evaluates specified validators and returns <c>True</c> if all of them are valid.
        /// </summary>
        /// <remarks>
        /// <p>
        /// Each validator can itself represent a collection of other validators if it is
        /// an instance of <see cref="ValidatorGroup"/> or one of its derived types.
        /// </p>
        /// <p>
        /// Please see the Validation Framework section in the documentation for more info.
        /// </p>
        /// </remarks>
        /// <param name="validationContext">Object to validate.</param>
        /// <param name="validators">Validators to evaluate.</param>
        /// <returns>
        /// <c>True</c> if all of the specified validators are valid, <c>False</c> otherwise.
        /// </returns>
        public bool Validate(object validationContext, params IValidator[] validators)
        {
            IDictionary contextParams = CreateValidatorParameters();
            bool result = true;
            foreach (IValidator validator in validators)
            {
                if (validator == null)
                {
                    throw new ArgumentException("Validator is not defined.");
                }
                result = validator.Validate(validationContext, contextParams, this.validationErrors) && result;
            }

            return result;
        }

        /// <summary>
        /// Gets the validation errors container.
        /// </summary>
        /// <value>The validation errors container.</value>
        public virtual IValidationErrors ValidationErrors
        {
            get { return validationErrors; }
        }

        /// <summary>
        /// Creates the validator parameters.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method can be overriden if you want to pass additional parameters
        /// to the validation framework, but you should make sure that you call
        /// this base implementation in order to add page, session, application,
        /// request, response and context to the variables collection.
        /// </para>
        /// </remarks>
        /// <returns>
        /// Dictionary containing parameters that should be passed to
        /// the data validation framework.
        /// </returns>
        protected virtual IDictionary CreateValidatorParameters()
        {
            IDictionary parameters = new ListDictionary();
            parameters["page"] = this.Page;
            parameters["usercontrol"] = this;
            parameters["session"] = this.Session;
            parameters["application"] = this.Application;
            parameters["request"] = this.Request;
            parameters["response"] = this.Response;
            parameters["context"] = this.Context;

            return parameters;
        }

        #endregion

        #region Data binding support

        /// <summary>
        /// Initializes the data bindings.
        /// </summary>
        protected virtual void InitializeDataBindings()
        {}

        /// <summary>
        /// Returns the key to be used for looking up a cached
        /// BindingManager instance in <see cref="SharedState"/>.
        /// </summary>
        /// <returns>a unique key identifying the <see cref="IBindingContainer"/> instance in the <see cref="SharedState"/> dictionary.</returns>
        protected virtual string GetBindingManagerKey()
        {
            return "DataBindingManager";
        }

        /// <summary>
        /// Creates a new <see cref="IBindingContainer"/> instance.
        /// </summary>
        /// <remarks>
        /// This factory method is called if no <see cref="IBindingContainer"/> could be found in <see cref="SharedState"/>
        /// using the key returned by <see cref="GetBindingManagerKey"/>.<br/>
        /// <br/>
        ///
        /// </remarks>
        /// <returns>a <see cref="IBindingContainer"/> instance to be used for DataBinding</returns>
        protected virtual IBindingContainer CreateBindingManager()
        {
            return new BaseBindingManager();
        }

        /// <summary>
        /// Gets the binding manager for this control.
        /// </summary>
        /// <value>The binding manager.</value>
        public IBindingContainer BindingManager
        {
            get { return this.bindingManager; }
        }

        /// <summary>
        /// Initializes binding manager and data bindings if necessary.
        /// </summary>
        private void InitializeBindingManager()
        {
            IDictionary sharedState = this.SharedState;

            string key = GetBindingManagerKey();
            this.bindingManager = sharedState[key] as BaseBindingManager;
            if (this.bindingManager == null)
            {
                lock(sharedState.SyncRoot)
                {
                    this.bindingManager = sharedState[key] as BaseBindingManager;
                    if (this.bindingManager == null)
                    {
                        try
                        {
                            this.bindingManager = CreateBindingManager();
                            if(bindingManager == null)
                            {
                                throw new ArgumentNullException("bindingManager",
                                                                "CreateBindingManager() must not return null");
                            }
                            InitializeDataBindings();
                        }
                        catch
                        {
                            this.bindingManager = null;
                            throw;
                        }
                        sharedState[key] = this.bindingManager;
                        this.OnDataBindingsInitialized(EventArgs.Empty);
                    }
                }
            }
        }

        /// <summary>
        /// Raises the <see cref="DataBindingsInitialized"/> event.
        /// </summary>
        protected virtual void OnDataBindingsInitialized(EventArgs e)
        {
            EventHandler handler = (EventHandler)base.Events[EventDataBindingsInitialized];

            if(handler != null)
            {
                handler(this, e);
            }
        }

        /// <summary>
        /// This event is raised after <see cref="BindingManager"/> as been initialized.
        /// </summary>
        public event EventHandler DataBindingsInitialized
        {
            add
            {
                base.Events.AddHandler(EventDataBindingsInitialized, value);
            }
            remove
            {
                base.Events.RemoveHandler(EventDataBindingsInitialized, value);
            }
        }

        /// <summary>
        /// Bind data from model to form.
        /// </summary>
        protected internal virtual void BindFormData()
        {
            if(BindingManager.HasBindings)
            {
                BindingManager.BindTargetToSource(this, Controller, Page.ValidationErrors);
            }
            OnDataBound(EventArgs.Empty);
        }

        /// <summary>
        /// Unbind data from form to model.
        /// </summary>
        protected internal virtual void UnbindFormData()
        {
            if(BindingManager.HasBindings)
            {
                BindingManager.BindSourceToTarget(this, Controller, Page.ValidationErrors);
            }
            OnDataUnbound(EventArgs.Empty);
        }

        /// <summary>
        /// This event is raised after all controls have been populated with values
        /// from the data model.
        /// </summary>
        public event EventHandler DataBound
        {
            add
            {
                base.Events.AddHandler(EventDataBound, value);
            }
            remove
            {
                base.Events.RemoveHandler(EventDataBound, value);
            }
        }

        /// <summary>
        /// Raises DataBound event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnDataBound(EventArgs e)
        {
            EventHandler handler = (EventHandler) base.Events[EventDataBound];
            if (handler != null)
            {
                handler(this, e);
            }
        }

        /// <summary>
        /// This event is raised after data model has been populated with values from
        /// web controls.
        /// </summary>
        public event EventHandler DataUnbound
        {
            add
            {
                base.Events.AddHandler(EventDataUnbound, value);
            }
            remove
            {
                base.Events.RemoveHandler(EventDataUnbound, value);
            }
        }

        /// <summary>
        /// Raises DataBound event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnDataUnbound(EventArgs e)
        {
            EventHandler handler = (EventHandler) base.Events[EventDataUnbound];
            if (handler != null)
            {
                handler(this, e);
            }
        }

        #endregion

        #region Application context support

        /// <summary>
        /// Gets or sets Spring application context of the page.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public virtual IApplicationContext ApplicationContext
        {
            get { return applicationContext; }
            set { applicationContext = value; }
        }

        #endregion

        #region Message source and localization support

        /// <summary>
        /// Gets or sets the localizer.
        /// </summary>
        /// <value>The localizer.</value>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ILocalizer Localizer
        {
            get { return localizer; }
            set
            {
                localizer = value;
                if (localizer.ResourceCache is NullResourceCache)
                {
                    localizer.ResourceCache = new AspNetResourceCache();
                }
            }
        }

        /// <summary>
        /// Gets or sets the local message source.
        /// </summary>
        /// <value>The local message source.</value>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public IMessageSource MessageSource
        {
            get { return messageSource; }
            set
            {
                messageSource = value;
                if (messageSource != null && messageSource is AbstractMessageSource)
                {
                    ((AbstractMessageSource) messageSource).ParentMessageSource = applicationContext;
                }
            }
        }

        /// <summary>
        /// Initializes local message source
        /// </summary>
        protected void InitializeMessageSource()
        {
            if (this.MessageSource == null)
            {
                string key = CreateSharedStateKey("MessageSource");
                IDictionary sharedState = this.SharedState;
                IMessageSource messageSource = sharedState[key] as IMessageSource;
                if (messageSource == null)
                {
                    lock(sharedState.SyncRoot)
                    {
                        messageSource = sharedState[key] as IMessageSource;
                        if (messageSource == null)
                        {
                            ResourceSetMessageSource defaultMessageSource = new ResourceSetMessageSource();
                            defaultMessageSource.UseCodeAsDefaultMessage = true;
                            ResourceManager rm = GetLocalResourceManager();
                            if (rm != null)
                            {
                                defaultMessageSource.ResourceManagers.Add(rm);
                            }
                            sharedState[key] = defaultMessageSource;
                            messageSource = defaultMessageSource;
                        }
                    }
                }
                this.MessageSource = messageSource;
            }
        }

        /// <summary>
        /// Creates and returns local ResourceManager for this page.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In ASP.NET 1.1, this method loads local resources from the web application assembly.
        /// </para>
        /// <para>
        /// However, in ASP.NET 2.0, local resources are compiled into the dynamic assembly,
        /// so we need to find that assembly instead and load the resources from it.
        /// </para>
        /// </remarks>
        /// <returns>Local ResourceManager instance.</returns>
        private ResourceManager GetLocalResourceManager()
#if !NET_2_0
        {
            return new ResourceManager(GetType().BaseType);
        }
#else
        {
            object resourceProvider = Page.GetLocalResourceProvider.Invoke(typeof(ResourceExpressionBuilder), new object[] {this});
            MethodInfo GetLocalResourceAssembly =
                    resourceProvider.GetType().GetMethod("GetLocalResourceAssembly", BindingFlags.NonPublic | BindingFlags.Instance);
            Assembly localResourceAssembly = (Assembly) GetLocalResourceAssembly.Invoke(resourceProvider, null);
            if (localResourceAssembly != null)
            {
                return new ResourceManager(VirtualPathUtility.GetFileName(this.AppRelativeVirtualPath), localResourceAssembly);
            }
            return null;
        }
#endif

        /// <summary>
        /// Returns message for the specified resource name.
        /// </summary>
        /// <param name="name">Resource name.</param>
        /// <returns>Message text.</returns>
        public string GetMessage(string name)
        {
            return messageSource.GetMessage(name, UserCulture);
        }

        /// <summary>
        /// Returns message for the specified resource name.
        /// </summary>
        /// <param name="name">Resource name.</param>
        /// <param name="args">Message arguments that will be used to format return value.</param>
        /// <returns>Formatted message text.</returns>
        public string GetMessage(string name, params object[] args)
        {
            return messageSource.GetMessage(name, UserCulture, args);
        }

        /// <summary>
        /// Returns resource object for the specified resource name.
        /// </summary>
        /// <param name="name">Resource name.</param>
        /// <returns>Resource object.</returns>
        public object GetResourceObject(string name)
        {
            return messageSource.GetResourceObject(name, UserCulture);
        }

        /// <summary>
        /// Gets or sets user's culture
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public virtual CultureInfo UserCulture
        {
            get { return Page.UserCulture; }
            set { Page.UserCulture = value; }
        }

        #endregion

        #region Spring Page support

        /// <summary>
        /// Overrides Page property to return <see cref="Spring.Web.UI.Page"/>
        /// instead of <see cref="System.Web.UI.Page"/>.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new Page Page
        {
            get { return (Page) base.Page; }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a key for shared state, taking into account whether
        /// this page belongs to a process or not.
        /// </summary>
        /// <param name="key">Key suffix</param>
        /// <returns>Generated unique shared state key.</returns>
        protected string CreateSharedStateKey(string key)
        {
            return key;
        }

        #endregion

        #region Dependency Injection Support

        /// <summary>
        /// Holds the default ApplicationContext to be used during DI.
        /// </summary>
        IApplicationContext ISupportsWebDependencyInjection.DefaultApplicationContext
        {
            get { return defaultApplicationContext; }
            set { defaultApplicationContext = value;  }
        }

        /// <summary>
        /// Injects dependencies into control before adding it.
        /// </summary>
        protected override void AddedControl(Control control,int index)
        {
            WebDependencyInjectionUtils.InjectDependenciesRecursive( defaultApplicationContext, control );
            base.AddedControl(control,index);
        }

        #endregion Dependency Injection Support
    }
}